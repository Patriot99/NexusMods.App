using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Activities;
using NexusMods.Abstractions.FileStore;
using NexusMods.Abstractions.HttpDownloader;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Networking.Downloaders.Interfaces;
using NexusMods.Networking.Downloaders.Tasks.State;
using NexusMods.Paths;
using NexusMods.Paths.Utilities;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace NexusMods.Networking.Downloaders.Tasks;

[Obsolete(message: "To be replaced with Jobs")]
public abstract class ADownloadTask : ReactiveObject, IDownloadTask
{
    private const int PollTimeMilliseconds = 1000;
    
    protected readonly IConnection Connection;
    protected readonly IActivityFactory ActivityFactory;
    /// <summary>
    /// The state of the download task, persisted to the database, will never
    /// be null after the .Create() method is called.
    /// </summary>
    protected HttpDownloaderState? TransientState = null!;
    protected ILogger<ADownloadTask> Logger;
    protected HttpClient HttpClient;
    protected IHttpDownloader HttpDownloader;
    protected CancellationTokenSource CancellationTokenSource;
    protected AbsolutePath _downloadPath = default!;
    protected IFileSystem FileSystem;
    protected IFileOriginRegistry FileOriginRegistry;
    private DownloaderState.ReadOnly _persistentState;
    private IDownloadService _downloadService;
    
    protected ADownloadTask(IServiceProvider provider)
    {
        Connection = provider.GetRequiredService<IConnection>();
        Logger = provider.GetRequiredService<ILogger<ADownloadTask>>();
        HttpClient = provider.GetRequiredService<HttpClient>();
        HttpDownloader = provider.GetRequiredService<IHttpDownloader>();
        CancellationTokenSource = new CancellationTokenSource();
        ActivityFactory = provider.GetRequiredService<IActivityFactory>();
        FileSystem = provider.GetRequiredService<IFileSystem>();
        FileOriginRegistry = provider.GetRequiredService<IFileOriginRegistry>();
        _downloadService = provider.GetRequiredService<IDownloadService>();
    }
    
    public void Init(DownloaderState.ReadOnly state)
    {
        PersistentState = state;
        Downloaded = state.Downloaded;
        _downloadPath = FileSystem.FromUnsanitizedFullPath(state.DownloadPath);
    }
    
    /// <summary>
    /// Reloads the state of the download task from the database.
    /// </summary>
    public void RefreshState()
    {
        PersistentState = PersistentState.Rebase();
    }

    /// <summary>
    /// Sets up the inital state of the download task, creates the persistent state
    /// and then returns for the parent class to fill out the source information.  
    /// </summary>
    protected EntityId Create(ITransaction tx)
    {
        // Add a subfolder for the download task
        var guid = Guid.NewGuid().ToString();
        var downloadSubfolder = _downloadService.OngoingDownloadsDirectory.Combine(guid);
        downloadSubfolder.CreateDirectory();
        
        _downloadPath = downloadSubfolder.Combine(guid).AppendExtension(KnownExtensions.Tmp);
        
        var state = new DownloaderState.New(tx)
        {
            FriendlyName = "<Unknown>",
            Status = DownloadTaskStatus.Idle,
            Downloaded = Size.Zero,
            DownloadPath = DownloadPath.ToString(),
        };
        return state.Id;
    }

    /// <summary>
    /// Perform the initialisation of the task, this should be called after the
    /// additional metadata has been added to the transaction.
    /// </summary>
    protected async Task Init(ITransaction tx, EntityId id)
    {
        var result = await tx.Commit();
        PersistentState = DownloaderState.Load(result.Db, result[id]);
    }
    
    protected async Task<(string Name, Size Size)> GetNameAndSizeAsync(Uri uri)
    {
        Logger.LogDebug("Getting name and size for {Url}", uri);
        if (uri.IsFile)
            return default;

        var response = await HttpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
        if (!response.IsSuccessStatusCode)
        {
            Logger.LogWarning("HTTP request {Url} failed with status {ResponseStatusCode}", uri, response.StatusCode);
            return default;
        }

        // Get the filename from the Content-Disposition header, or default to a temporary file name.
        var contentDispositionHeader = response.Content.Headers.ContentDisposition?.FileNameStar
                                       ?? response.Content.Headers.ContentDisposition?.FileName
                                       ?? Path.GetTempFileName();

        var name = contentDispositionHeader.Trim('"');
        var size = Size.From((ulong)response.Content.Headers.ContentLength.GetValueOrDefault(0));
        return (name, size);
    }
    
    protected async Task SetStatus(DownloadTaskStatus status)
    {
        using var tx = Connection.BeginTransaction();
        tx.Add(PersistentState.Id, DownloaderState.Status, status);
        
        if (TransientState != null)
        {
            var report = TransientState.ActivityStatus?.MakeTypedReport();
            if (report == null || !report.Current.HasValue)
            {
                tx.Add(PersistentState.Id, DownloaderState.Downloaded, Size.Zero);
            }
            else
            {
                tx.Add(PersistentState.Id, DownloaderState.Downloaded, report.Current.Value);
            }
        }
        
        await tx.Commit();
        RefreshState();
    }
    
    protected async Task MarkComplete()
    {
        using var tx = Connection.BeginTransaction();
        tx.Add(PersistentState.Id, DownloaderState.Status, DownloadTaskStatus.Completed);
        tx.Add(PersistentState.Id, CompletedDownloadState.CompletedDateTime, DateTime.Now);
        await tx.Commit();
        RefreshState();
    }
    
    [Reactive]
    public DownloaderState.ReadOnly PersistentState { get; protected set; }
    
    public AbsolutePath DownloadPath => _downloadPath;


    [Reactive] public Bandwidth Bandwidth { get; set; } = Bandwidth.From(0);

    [Reactive] public Size Downloaded { get; set; } = Size.From(0);

    [Reactive] public Percent Progress { get; set; } = Percent.Zero;

    
    /// <inheritdoc />
    public async Task StartAsync()
    {
        await Resume();
    }

    /// <inheritdoc />
    public async Task Cancel()
    {
        try { await CancellationTokenSource.CancelAsync(); }
        catch (Exception) { /* ignored */ }
        await SetStatus(DownloadTaskStatus.Cancelled);
        
        // Cleanup the download directory (this could actually still be in use by the downloader,
        // since CancelAsync is not guaranteed to wait for exception handlers to finish handling the cancellation)
        CleanupDownloadFiles();
    }

    /// <inheritdoc />
    public async Task Suspend()
    {
        await SetStatus(DownloadTaskStatus.Paused);
        try { await CancellationTokenSource.CancelAsync(); }
        catch (Exception) { /* ignored */ }
        
        // Replace the token source.
        CancellationTokenSource = new CancellationTokenSource();
    }
    
    /// <inheritdoc />
    public async Task Resume()
    {
        Logger.LogInformation("Starting download of {Name}", PersistentState.FriendlyName);
        await SetStatus(DownloadTaskStatus.Downloading);
        TransientState = new HttpDownloaderState
        {
            Activity = ActivityFactory.Create<Size>(IHttpDownloader.Group, "Downloading {FileName}", DownloadPath),
        };
        _ = StartActivityUpdater();
        
        Logger.LogDebug("Dispatching download task for {Name}", PersistentState.FriendlyName);
        try
        {
            await Download(DownloadPath, CancellationTokenSource.Token);
        }
        catch (OperationCanceledException e)
        {
            return;
        }
        
        UpdateActivity();
        await SetStatus(DownloadTaskStatus.Analyzing);
        Logger.LogInformation("Finished download of {Name} starting analysis", PersistentState.FriendlyName);
        await AnalyzeFile();
        await MarkComplete();
        CleanupDownloadFiles();
    }

    // Delete the download task subfolder and all files within it. 
    private void CleanupDownloadFiles()
    {
        DownloadPath.Parent.DeleteDirectory(recursive: true);
    }

    private async Task AnalyzeFile()
    {
        try
        {
            await FileOriginRegistry.RegisterDownload(DownloadPath, PersistentState.Id, PersistentState.FriendlyName);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to analyze file {Name}", PersistentState.FriendlyName);
        }
    }

    private async Task StartActivityUpdater()
    {
        while (PersistentState.Status == DownloadTaskStatus.Downloading)
        {
            UpdateActivity();
            await Task.Delay(PollTimeMilliseconds);
        }
    }

    private void UpdateActivity()
    {
        try
        {
            var report = TransientState!.ActivityStatus?.MakeTypedReport();
            if (report is { Current.HasValue: true })
            {
                Downloaded = report.Current.Value;
                if (DownloaderState.Size.TryGet(PersistentState, out var size) && size != Size.Zero)
                    Progress = Percent.CreateClamped((long)Downloaded.Value, (long)size.Value);
                if (report.Throughput.HasValue)
                    Bandwidth = Bandwidth.From(report.Throughput.Value.Value);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to update activity status");
        }
    }
    
    /// <inheritdoc />
    public void SetIsHidden(bool isHidden, ITransaction tx)
    {
        if (PersistentState.Status != DownloadTaskStatus.Completed) return;
        tx.Add(PersistentState.Id, CompletedDownloadState.Hidden, isHidden);
    }
    
    /// <summary>
    /// Begin the process of downloading a file to the specified destination, should
    /// terminate when the download is complete or cancelled. The destination may have
    /// vestigial data from a previous download attempt, if the downloader can resume
    /// using this data it should do so. The method should not return until the download
    /// has completed or been cancelled.
    /// </summary>
    protected abstract Task Download(AbsolutePath destination, CancellationToken token);
}

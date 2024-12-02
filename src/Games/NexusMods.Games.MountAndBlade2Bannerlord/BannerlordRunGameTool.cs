using Bannerlord.LauncherManager.Utils;
using Bannerlord.ModuleManager;
using CliWrap;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.GameLocators;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Games.Generic;
using static Bannerlord.LauncherManager.Constants;
namespace NexusMods.Games.MountAndBlade2Bannerlord;

/// <summary>
/// This is to run the game or SMAPI using the shell, which allows them to start their own console,
/// allowing users to interact with it.
/// </summary>
public class BannerlordRunGameTool : RunGameTool<Bannerlord>
{
    private readonly ILogger<BannerlordRunGameTool> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly GameToolRunner _runner;

    public BannerlordRunGameTool(IServiceProvider serviceProvider, Bannerlord game, GameToolRunner runner)
        : base(serviceProvider, game)
    {
        _serviceProvider = serviceProvider;
        _runner = runner;
        _logger = serviceProvider.GetRequiredService<ILogger<BannerlordRunGameTool>>();
    }

    protected override bool UseShell { get; set; } = false;
    
    public override async Task Execute(Loadout.ReadOnly loadout, CancellationToken cancellationToken, string[]? commandLineArgs)
    {
        commandLineArgs ??= [];

        // We need to 'inject' the current set of enabled modules in addition to any existing parameters.
        // This way, external arguments specified by outside entities are preserved.
        var args = await GetBannerlordExeCommandlineArgs(loadout, commandLineArgs, cancellationToken);
        var install = loadout.InstallationInstance;
        var exe = install.LocationsRegister[LocationId.Game];
        if (install.Store != GameStore.XboxGamePass) { exe = exe/BinFolder/Win64Configuration/BannerlordExecutable; }
        else { exe = exe/BinFolder/XboxConfiguration/BannerlordExecutable; }

        var command = Cli.Wrap(exe.ToString())
            .WithArguments(args)
            .WithWorkingDirectory(exe.Parent.ToString());

        // Note(sewer): We use the tool runner to execute an alternative process,
        //              but because the UI expects `RunGameTool`, we override the `RunGameTool's` executor
        //              with `GameToolRunner` implementation.
        await _runner.ExecuteAsync(loadout, command, true, cancellationToken);
    }

    // TODO: Refactor Bannerlord.LauncherManager so we don't have to copy (some) of the commandline logic.
    //       (Since LauncherManager has dependencies on code we don't need/want to provide).
    private async Task<string[]> GetBannerlordExeCommandlineArgs(Loadout.ReadOnly loadout, string[] commandLineArgs, CancellationToken cancellationToken)
    {
        // Set the (automatic) load order.
        // Copied from Bannerlord.LauncherManager
        var manifestPipeline = Pipelines.GetManifestPipeline(_serviceProvider);
        var modules = (await Helpers.GetAllManifestsAsync(_logger, loadout, manifestPipeline, cancellationToken).ToArrayAsync(cancellationToken))
            .Select(x => x.Item2);
        var sortedModules = AutoSort(Hack.GetDummyBaseGameModules()
            .Concat(modules)).Select(x => x.Id).ToArray();
        var loadOrderCli = sortedModules.Length > 0 ? $"_MODULES_*{string.Join("*", sortedModules)}*_MODULES_" : string.Empty;

        // Add the new arguments
        return commandLineArgs.Concat(["/singleplayer", loadOrderCli]).ToArray();
    }
    
    // Copied from Bannerlord.LauncherManager
    // needs upstream changes, will do those changes tomorrow (21st Nov 2024)
    private static IEnumerable<ModuleInfoExtended> AutoSort(IEnumerable<ModuleInfoExtended> source)
    {
        var orderedModules = source
            .OrderByDescending(x => x.IsOfficial)
            .ThenBy(x => x.Id, new AlphanumComparatorFast())
            .ToArray();

        return ModuleSorter.TopologySort(orderedModules, module => ModuleUtilities.GetDependencies(orderedModules, module));
    }
}
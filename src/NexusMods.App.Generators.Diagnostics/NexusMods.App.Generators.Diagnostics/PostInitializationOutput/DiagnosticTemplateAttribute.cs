namespace NexusMods.App.Generators.Diagnostics.PostInitializationOutput;

public class DiagnosticTemplateAttribute
{
    public const string Name = nameof(DiagnosticTemplateAttribute);
    public const string GlobalName = $"global::{Constants.Namespace}.{Name}";
    public const string HintName = $"{Name}.g.cs";

    public const string SourceCode = /*lang=csharp*/
        $$"""
{{Constants.AutoGeneratedHeader}}

namespace {{Constants.Namespace}}
{
    /// <summary>
    /// Indicates that field is a diagnostic template.
    /// </summary>
    {{Constants.CodeCoverageAttribute}}
    [global::System.AttributeUsage(global::System.AttributeTargets.Field)]
    internal class {{Name}} : global::System.Attribute { }
}
""";
}

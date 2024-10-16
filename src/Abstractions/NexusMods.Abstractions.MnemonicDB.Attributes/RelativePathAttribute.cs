using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Abstractions.Attributes;
using NexusMods.MnemonicDB.Abstractions.ElementComparers;
using NexusMods.Paths;

namespace NexusMods.Abstractions.MnemonicDB.Attributes;

/// <summary>
/// Represents a relative path.
/// </summary>
public class RelativePathAttribute(string ns, string name) : ScalarAttribute<RelativePath, string>(ValueTag.Utf8Insensitive, ns, name)
{
    /// <inheritdoc />
    protected override string ToLowLevel(RelativePath value)
    {
        return value.Path;
    }

    /// <inheritdoc />
    protected override RelativePath FromLowLevel(string value, AttributeResolver resolver)
    {
        return RelativePath.FromUnsanitizedInput(value);
    }
}

using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Abstractions.Attributes;
using NexusMods.MnemonicDB.Abstractions.ElementComparers;

namespace NexusMods.Abstractions.MnemonicDB.Attributes;

/// <summary>
/// A MneumonicDB attribute for a ulong value
/// </summary>
public class ULongAttribute(string ns, string name) : ScalarAttribute<ulong, ulong>(ValueTag.UInt64, ns, name)
{
    protected override ulong ToLowLevel(ulong value) => value;
    
    protected override ulong FromLowLevel(ulong value, AttributeResolver resolver) => value;
}

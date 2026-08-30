namespace Periphery.Hid.Tests;

/// <summary>
/// Tests that mutate <see cref="HidQuirks"/>'s static registry share this
/// collection so they run sequentially. xUnit runs different classes in
/// parallel by default; two test classes calling
/// <c>HidQuirks.ResetForTests()</c> concurrently race against each
/// other's RegisterUps calls.
/// </summary>
[CollectionDefinition(nameof(HidQuirksTestCollection))]
public sealed class HidQuirksTestCollection { }

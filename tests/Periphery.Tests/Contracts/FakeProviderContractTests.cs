namespace Periphery.Tests;

/// <summary>
/// Runs the full <see cref="DeviceProviderContractTests"/> suite against
/// <see cref="FakeDeviceProvider"/> — the same fake used by all other unit tests.
/// </summary>
/// <remarks>
/// If the fake breaks the contract it is supposed to simulate, every test
/// that depends on it is testing a fiction. This class keeps the fake honest.
/// </remarks>
public sealed class FakeProviderContractTests : DeviceProviderContractTests
{
    protected override IAsyncEnumerable<DeviceInfo> Enumerate(
        DeviceInfo[] seeds, DeviceFilter filter, CancellationToken ct = default)
        => new FakeDeviceProvider(seeds).EnumerateAsync(filter, ct);
}

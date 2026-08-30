using Periphery.Bootloader;

namespace Periphery.Treehopper.Flasher.Tests;

/// <summary>
/// The Treehopper composition's concurrency posture (ADR-0063 DEC-005): correlation is always the exact
/// <see cref="DeviceCorrelationMode.ByLocationPath"/>, and concurrent EFM8 flashing is <b>on by default</b>
/// — hardware-verified safe (two boards, overlapping upload windows, zero corruption), with #220's physical
/// bus-collision hypothesis disproven. The <c>allowConcurrentEfm8Flash</c> flag is the opt-<em>out</em> that
/// forces serialization. These assert the runtime cap directly.
/// </summary>
public class TreehopperFlasherCompositionTests
{
    [Fact]
    public async Task Defaults_to_concurrent_efm8_flashing()
    {
        await using var svc = TreehopperFlasher.CreateService();
        // Concurrent-by-default: topology correlation flashes several boards at once, each addressed by its port.
        Assert.Equal(FlashAnything.FlashAnythingService.DefaultMaxFlashConcurrency, svc.MaxFlashConcurrency);
    }

    [Fact]
    public async Task Opt_out_forces_serialized_efm8_flashing()
    {
        await using var svc = TreehopperFlasher.CreateService(allowConcurrentEfm8Flash: false);
        // The opt-out (conservative fallback / debugging aid): one board in flight at a time.
        Assert.Equal(1, svc.MaxFlashConcurrency);
    }
}

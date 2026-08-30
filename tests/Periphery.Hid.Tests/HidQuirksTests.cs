using System.Threading;
using System.Threading.Tasks;

namespace Periphery.Hid.Tests;

/// <summary>
/// Behaviour pinning for <see cref="HidQuirks"/> — the registry semantics
/// other Periphery.Hid consumers depend on (last-write-wins, override
/// event firing, TryRegister collision handling).
/// </summary>
[Collection(nameof(HidQuirksTestCollection))]
public class HidQuirksTests : IDisposable
{
    public HidQuirksTests()
    {
        // Each test starts from the baseline registration set
        // (WayTech 0665:5161 → MegatecQxCodec) so order-of-execution
        // doesn't matter.
        HidQuirks.ResetForTests();
    }

    public void Dispose()
    {
        // Detach test event handlers so they don't leak across tests.
        // Easiest by resetting again — Reset rebuilds the baseline but
        // the event has no listeners after disposal.
        HidQuirks.ResetForTests();
    }

    [Fact]
    public void Baseline_RegistersWayTech()
    {
        var codec = HidQuirks.GetUpsCodec(new HardwareId(0x0665), new HardwareId(0x5161));
        Assert.NotNull(codec);
        Assert.IsType<MegatecQxCodec>(codec);
    }

    [Fact]
    public void GetUpsCodec_UnregisteredVidPid_ReturnsNull()
    {
        var codec = HidQuirks.GetUpsCodec(new HardwareId(0x0001), new HardwareId(0x0001));
        Assert.Null(codec);
    }

    [Fact]
    public void RegisterUps_NewVidPid_RegistersWithoutFiringOverride()
    {
        bool overrideFired = false;
        HidQuirks.UpsCodecOverridden += (_, _) => overrideFired = true;

        var custom = new FakeCodec();
        HidQuirks.RegisterUps(new HardwareId(0xBEEF), new HardwareId(0xCAFE), custom);

        Assert.Same(custom, HidQuirks.GetUpsCodec(new HardwareId(0xBEEF), new HardwareId(0xCAFE)));
        Assert.False(overrideFired);
    }

    [Fact]
    public void RegisterUps_ExistingVidPid_ReplacesAndFiresOverride()
    {
        HardwareId? capturedVid = null;
        HardwareId? capturedPid = null;
        HidQuirks.UpsCodecOverridden += (vid, pid) =>
        {
            capturedVid = vid;
            capturedPid = pid;
        };

        // WayTech is in the baseline; replacing it triggers the event.
        var replacement = new FakeCodec();
        HidQuirks.RegisterUps(new HardwareId(0x0665), new HardwareId(0x5161), replacement);

        Assert.Same(replacement, HidQuirks.GetUpsCodec(new HardwareId(0x0665), new HardwareId(0x5161)));
        Assert.Equal(new HardwareId(0x0665), capturedVid);
        Assert.Equal(new HardwareId(0x5161), capturedPid);
    }

    [Fact]
    public void TryRegisterUps_NewVidPid_ReturnsTrue()
    {
        var ok = HidQuirks.TryRegisterUps(
            new HardwareId(0xDEAD), new HardwareId(0xBEEF), new FakeCodec(),
            out bool wasOverride);

        Assert.True(ok);
        Assert.False(wasOverride);
    }

    [Fact]
    public void TryRegisterUps_ExistingVidPid_ReturnsFalse_DoesNotReplace()
    {
        var baselineCodec = HidQuirks.GetUpsCodec(new HardwareId(0x0665), new HardwareId(0x5161));
        Assert.NotNull(baselineCodec);

        var newCodec = new FakeCodec();
        var ok = HidQuirks.TryRegisterUps(
            new HardwareId(0x0665), new HardwareId(0x5161), newCodec,
            out bool wasOverride);

        Assert.False(ok);
        Assert.True(wasOverride);
        // Original baseline codec still in place; new one wasn't stored.
        Assert.Same(baselineCodec, HidQuirks.GetUpsCodec(new HardwareId(0x0665), new HardwareId(0x5161)));
    }

    [Fact]
    public void RegisterUps_NullCodec_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            HidQuirks.RegisterUps(new HardwareId(0x0001), new HardwareId(0x0001), null!));
    }

    private sealed class FakeCodec : IHidUpsCodec
    {
        public ValueTask<HidBatterySnapshot> ReadSnapshotAsync(HidDevice device, CancellationToken ct)
            => ValueTask.FromResult(new HidBatterySnapshot(null, null, null, null));
    }
}

using System.Collections.Immutable;

namespace Periphery.FlashAnything.Tests;

/// <summary>
/// The policy half of probe autoflash (adr.md Decision 8). Rule 2 used to be a flat ban on
/// probe-identified targets; it is now a scope check against the bridges the operator bound.
/// </summary>
public class ProbeAutoflashPolicyTests
{
    private const string Family = "STM32 UART (AN3155)";

    private static DeviceInfo Bridge(string serial = "92EA014C", string location = "PCIROOT(0)#USB(1)")
        => new()
        {
            Id = new DeviceId($@"USB\VID_10C4&PID_EA60\{serial}"),
            Name = "Silicon Labs CP210x USB to UART Bridge",
            VendorId = new HardwareId(0x10C4),
            ProductId = new HardwareId(0xEA60),
            SerialNumber = serial,
            LocationPath = location,
        };

    private static BridgeIdentity IdentityOf(DeviceInfo d)
    {
        Assert.True(BridgeIdentity.TryFrom(d, out var id, out string? why), why);
        return id;
    }

    private static FlashTargetView Target(BridgeIdentity? bridge) => new(
        Id: new DeviceId("COM7"),
        DisplayName: "STM32 UART",
        ProviderName: Family,
        Identification: IdentificationMode.Probe,
        Bridge: bridge);

    private static AutoflashConfig Armed(params BridgeIdentity[] bridges) =>
        new(Family, FlashOptions.Default) { Bridges = bridges.ToImmutableHashSet() };

    private static readonly ImmutableHashSet<DeviceId> None = ImmutableHashSet<DeviceId>.Empty;

    // ── the scope check ──

    [Fact]
    public void A_probe_target_on_a_bound_bridge_is_flashed()
    {
        var bridge = IdentityOf(Bridge());

        var action = AutoflashPolicy.Decide(Armed(bridge), Target(bridge), None);

        Assert.IsType<AutoflashAction.Flash>(action);
    }

    [Fact]
    public void A_probe_target_on_an_unbound_bridge_is_skipped()
    {
        // The operator bound the fixture on one port; this is a different CP210x on another.
        var bound = IdentityOf(Bridge(serial: "AAAA1111", location: "PCIROOT(0)#USB(1)"));
        var other = IdentityOf(Bridge(serial: "BBBB2222", location: "PCIROOT(0)#USB(4)"));

        var action = AutoflashPolicy.Decide(Armed(bound), Target(other), None);

        var skip = Assert.IsType<AutoflashAction.Skip>(action);
        Assert.Contains("not on a bound bridge", skip.Reason);
    }

    [Fact]
    public void A_probe_target_with_no_arm_bound_is_skipped()
    {
        // The old flat ban, now expressed as an empty scope: arming for a probe family without
        // binding anything authorises nothing.
        var action = AutoflashPolicy.Decide(Armed(), Target(IdentityOf(Bridge())), None);

        Assert.IsType<AutoflashAction.Skip>(action);
    }

    [Fact]
    public void A_probe_target_whose_bridge_could_not_be_identified_is_skipped()
    {
        // Ineligible, not a wildcard. A target we cannot attribute to a bridge must never match a
        // bound one.
        var action = AutoflashPolicy.Decide(Armed(IdentityOf(Bridge())), Target(bridge: null), None);

        var skip = Assert.IsType<AutoflashAction.Skip>(action);
        Assert.Contains("could not be identified", skip.Reason);
    }

    [Fact]
    public void A_bound_bridge_does_not_widen_the_armed_family()
    {
        // Binding a fixture authorises probing it, not flashing whatever turns up on it.
        var bridge = IdentityOf(Bridge());
        var otherFamily = Target(bridge) with { ProviderName = "EFM8 USB-HID" };

        var skip = Assert.IsType<AutoflashAction.Skip>(
            AutoflashPolicy.Decide(Armed(bridge), otherFamily, None));

        Assert.Contains("not the armed family", skip.Reason);
    }

    [Fact]
    public void Passive_targets_are_unaffected_by_the_scope_check()
    {
        // With no bridges bound, a passive target still flashes — the check short-circuits on
        // Identification before it ever looks at the bridge.
        var passive = Target(bridge: null) with { Identification = IdentificationMode.Passive };

        Assert.IsType<AutoflashAction.Flash>(AutoflashPolicy.Decide(Armed(), passive, None));
    }

    [Fact]
    public void A_bound_probe_target_already_flashed_is_still_skipped()
    {
        // Binding does not weaken idempotence; rule 3 still applies.
        var bridge = IdentityOf(Bridge());
        var target = Target(bridge);

        var skip = Assert.IsType<AutoflashAction.Skip>(
            AutoflashPolicy.Decide(Armed(bridge), target, ImmutableHashSet.Create(target.Id)));

        Assert.Contains("already flashed", skip.Reason);
    }

    // ── what may be bound ──

    [Fact]
    public void A_bridge_with_neither_serial_nor_port_cannot_be_bound()
    {
        // VID/PID names a model, not a device. Binding on it would authorise probing every CH340
        // on the bench, so the arm has to fail rather than bind something ambiguous.
        var anonymous = Bridge() with { SerialNumber = null, LocationPath = null };

        Assert.False(BridgeIdentity.TryFrom(anonymous, out _, out string? reason));
        Assert.Contains("cannot be told apart", reason);
    }

    [Fact]
    public void A_bridge_with_a_port_but_no_serial_can_be_bound()
    {
        // The common case: CH340s expose no serial number at all.
        var noSerial = Bridge() with { SerialNumber = null };

        Assert.True(BridgeIdentity.TryFrom(noSerial, out var id, out _));
        Assert.Null(id.SerialNumber);
        Assert.Equal("PCIROOT(0)#USB(1)", id.LocationPath);
    }

    [Fact]
    public void A_device_with_no_usb_ids_cannot_be_bound()
    {
        var bare = new DeviceInfo { Id = new DeviceId("COM9"), Name = "some port" };

        Assert.False(BridgeIdentity.TryFrom(bare, out _, out string? reason));
        Assert.Contains("no USB vendor/product id", reason);
    }

    [Fact]
    public void Identity_survives_a_replug_that_changes_casing()
    {
        // Windows re-enumerates the same device with different casing (issue #231), and a bind that
        // stopped matching afterwards would silently disarm the fixture.
        var lower = IdentityOf(Bridge(serial: "92ea014c", location: "pciroot(0)#usb(1)"));
        var upper = IdentityOf(Bridge(serial: "92EA014C", location: "PCIROOT(0)#USB(1)"));

        Assert.Equal(lower, upper);
        Assert.Equal(lower.GetHashCode(), upper.GetHashCode());
        Assert.IsType<AutoflashAction.Flash>(AutoflashPolicy.Decide(Armed(lower), Target(upper), None));
    }

    [Fact]
    public void Same_model_in_a_different_port_is_a_different_bridge()
    {
        var a = IdentityOf(Bridge(serial: null!, location: "PCIROOT(0)#USB(1)") with { SerialNumber = null });
        var b = IdentityOf(Bridge(serial: null!, location: "PCIROOT(0)#USB(4)") with { SerialNumber = null });

        Assert.NotEqual(a, b);
    }
}

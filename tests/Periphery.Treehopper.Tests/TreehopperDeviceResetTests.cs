using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Periphery.Treehopper.Tests;

/// <summary>
/// Unit tests for the pure logic of <see cref="TreehopperDeviceReset"/>: which strategies
/// it advertises (the prepended <see cref="ResetKind.SoftProtocol"/> rung, gentlest-first)
/// and how it routes <see cref="TreehopperDeviceReset.ResetAsync"/> — soft to the local
/// board path (gated on a Treehopper), everything else to the wrapped reset. The reboot
/// wire opcode itself is covered by <c>WireEncodeTests</c> / <c>BoardPeripheralTests</c>;
/// the live open+reboot path needs hardware and is exercised on the bench, not here.
/// </summary>
public class TreehopperDeviceResetTests
{
    private static readonly ResetStrategy PortCycle =
        new(ResetKind.UsbPortCycle, ResetBlastRadius.Self, ReEnumerates: true);
    private static readonly ResetStrategy DisableEnable =
        new(ResetKind.PnpDisableEnable, ResetBlastRadius.Self, ReEnumerates: false);

    private static DeviceInfo Treehopper() => new()
    {
        Id = @"\\?\usb#vid_10c4&pid_8a7e#JXNQA4BF#{a5dcbf10-6530-11d2-901f-00c04fb951ed}",
        Name = "UserInterface",
        VendorId = TreehopperBoard.Vid,
        ProductId = TreehopperBoard.Pid,
    };

    private static DeviceInfo NotTreehopper() => new()
    {
        Id = "USB\\VID_2109&PID_2817\\6&abc",
        Name = "Some Hub",
        VendorId = new HardwareId(0x2109),
        ProductId = new HardwareId(0x2817),
    };

    /// <summary>Records calls; advertises a fixed strategy set, returns a fixed outcome.</summary>
    private sealed class FakeInner(params ResetStrategy[] strategies) : IDeviceReset
    {
        public int ResetCalls { get; private set; }
        public ResetStrategy? LastStrategy { get; private set; }
        public ResetOutcome OutcomeToReturn { get; init; } = ResetOutcome.Issued;

        public IReadOnlyList<ResetStrategy> StrategiesFor(DeviceInfo device) => strategies;

        public ValueTask<ResetOutcome> ResetAsync(DeviceInfo device, ResetStrategy strategy, CancellationToken ct)
        {
            ResetCalls++;
            LastStrategy = strategy;
            return new(OutcomeToReturn);
        }
    }

    [Fact]
    public void StrategiesFor_Treehopper_PrependsBothSoftRungsGentlestFirst()
    {
        var inner = new FakeInner(PortCycle, DisableEnable);
        var reset = new TreehopperDeviceReset(inner);

        var strategies = reset.StrategiesFor(Treehopper());

        Assert.Equal(4, strategies.Count);
        Assert.Equal(ResetKind.SoftProtocol, strategies[0].Kind);
        Assert.True(strategies[0].ReEnumerates);                       // a reboot re-enumerates
        Assert.Equal(ResetBlastRadius.Self, strategies[0].Radius);     // affects only this board

        // The out-of-band rescue sits between the cooperative reboot and anything that touches
        // the bus: it is the rung that still lands when the foreground has stopped (ADR-0075).
        Assert.Equal(ResetKind.SoftProtocolOutOfBand, strategies[1].Kind);
        Assert.True(strategies[1].ReEnumerates);                       // a full MCU reset re-enumerates
        Assert.Equal(ResetBlastRadius.Self, strategies[1].Radius);     // EP0 disturbs no sibling

        Assert.Equal(ResetKind.UsbPortCycle, strategies[2].Kind);      // inner list preserved, in order
        Assert.Equal(ResetKind.PnpDisableEnable, strategies[3].Kind);
    }

    [Fact]
    public void StrategiesFor_Treehopper_OrdersTheTwoSoftRungsCooperativeFirst()
    {
        // The ordering is the contract IRecoveryPolicy reads ("gentlest first"), and it is the
        // whole reason these are two strategies rather than one with a hidden fallback: a policy
        // must be able to pick the reachable rung without being handed the unreachable one first.
        var reset = new TreehopperDeviceReset(new FakeInner());

        var kinds = reset.StrategiesFor(Treehopper()).Select(s => s.Kind).ToArray();

        Assert.Equal([ResetKind.SoftProtocol, ResetKind.SoftProtocolOutOfBand], kinds);
    }

    [Fact]
    public void StrategiesFor_NonTreehopper_DelegatesUnchanged()
    {
        var inner = new FakeInner(PortCycle, DisableEnable);
        var reset = new TreehopperDeviceReset(inner);

        var strategies = reset.StrategiesFor(NotTreehopper());

        Assert.Equal(2, strategies.Count);
        Assert.Equal(ResetKind.UsbPortCycle, strategies[0].Kind);
        Assert.Equal(ResetKind.PnpDisableEnable, strategies[1].Kind);
    }

    [Fact]
    public void StrategiesFor_TreehopperWithNoInnerStrategies_StillAdvertisesBothSoftRungs()
    {
        // Even if the platform reset advertises nothing (e.g. it can't resolve the hub), a
        // Treehopper can still be reset over its own transport — by either soft rung. This is
        // the unelevated case that matters most on a kiosk: the platform rungs need elevation,
        // opening a USB device and sending a request does not.
        var inner = new FakeInner();
        var reset = new TreehopperDeviceReset(inner);

        var strategies = reset.StrategiesFor(Treehopper());

        Assert.Equal(2, strategies.Count);
        Assert.Equal(ResetKind.SoftProtocol, strategies[0].Kind);
        Assert.Equal(ResetKind.SoftProtocolOutOfBand, strategies[1].Kind);
    }

    [Fact]
    public async Task ResetAsync_OutOfBandOnNonTreehopper_DelegatesToInner()
    {
        // Same gating as the cooperative soft rung: the EP0 vendor contract is Treehopper
        // firmware's, so aiming it at another device is the inner reset's concern. Without this
        // arm the decorator would fire a vendor request at arbitrary hardware.
        var outOfBand = new ResetStrategy(ResetKind.SoftProtocolOutOfBand, ResetBlastRadius.Self, ReEnumerates: true);
        var inner = new FakeInner();
        var reset = new TreehopperDeviceReset(inner);

        var outcome = await reset.ResetAsync(NotTreehopper(), outOfBand, CancellationToken.None);

        Assert.Equal(1, inner.ResetCalls);
        Assert.Equal(ResetKind.SoftProtocolOutOfBand, inner.LastStrategy!.Value.Kind);
        Assert.Equal(ResetOutcome.Issued, outcome);
    }

    [Fact]
    public async Task ResetAsync_NonSoftStrategy_DelegatesToInner()
    {
        var inner = new FakeInner(PortCycle) { OutcomeToReturn = ResetOutcome.Degraded };
        var reset = new TreehopperDeviceReset(inner);

        var outcome = await reset.ResetAsync(Treehopper(), PortCycle, CancellationToken.None);

        Assert.Equal(1, inner.ResetCalls);
        Assert.Equal(ResetKind.UsbPortCycle, inner.LastStrategy!.Value.Kind);
        Assert.Equal(ResetOutcome.Degraded, outcome);                  // inner's outcome flows through
    }

    [Fact]
    public async Task ResetAsync_SoftStrategyOnNonTreehopper_DelegatesToInner()
    {
        // Gating: a SoftProtocol strategy aimed at a non-Treehopper device is the inner
        // reset's concern, not ours — we never try to open it as a board.
        var soft = new ResetStrategy(ResetKind.SoftProtocol, ResetBlastRadius.Self, ReEnumerates: true);
        var inner = new FakeInner();
        var reset = new TreehopperDeviceReset(inner);

        var outcome = await reset.ResetAsync(NotTreehopper(), soft, CancellationToken.None);

        Assert.Equal(1, inner.ResetCalls);
        Assert.Equal(ResetOutcome.Issued, outcome);
    }
}

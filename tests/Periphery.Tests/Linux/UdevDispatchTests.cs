using System.Collections.Immutable;
using Periphery.Linux.Core;

namespace Periphery.Tests.Linux;

/// <summary>
/// The Linux monitor's udev dispatch as a pure step. Each test feeds <see cref="UdevDispatch.Step"/>
/// an event and the devices held, and reads the edges and the devices held after it.
/// </summary>
public class UdevDispatchTests
{
    // An RFCOMM tty moves under the connection's hci device when its channel connects, and back when
    // the port shuts down (#304). The paths are shaped like sysfs, not captured.
    private const string Detached = "/sys/devices/virtual/tty/rfcomm0";
    private const string Attached = "/sys/devices/virtual/bluetooth/hci0/hci0:256/tty/rfcomm0";

    private static readonly ImmutableDictionary<DeviceId, DeviceInfo> None = ImmutableDictionary<DeviceId, DeviceInfo>.Empty;

    private static DeviceInfo Port(string syspath, bool active = false) =>
        new() { Id = syspath, Name = "rfcomm0", Category = DeviceCategory.Ports, IsActive = active };

    private static ImmutableDictionary<DeviceId, DeviceInfo> Holding(params DeviceInfo[] devices) =>
        devices.ToImmutableDictionary(d => d.Id);

    private static (UdevEdgeKind Kind, string Id)[] Edges(UdevStep step) =>
        [.. step.Edges.Select(e => (e.Kind, e.Device.Id.Value))];

    // ── move (#304) ────────────────────────────────────────────────────

    [Fact]
    public void Move_OfAHeldDevice_IsTheOldIdLeaving_ThenTheNewOneArriving()
    {
        var step = UdevDispatch.Step(Holding(Port(Detached)), new("move", Attached, Port(Attached), Detached));

        Assert.Equal(new[] { (UdevEdgeKind.Disappeared, Detached), (UdevEdgeKind.Appeared, Attached) }, Edges(step));
        Assert.Equal("rfcomm0", step.Edges[0].Device.Name);
        Assert.Equal(new DeviceId[] { Attached }, step.Devices.Keys);
    }

    [Fact]
    public void Move_OfAnActiveDevice_ActivatesItUnderTheNewId()
    {
        var step = UdevDispatch.Step(Holding(Port(Detached, active: true)), new("move", Attached, Port(Attached, active: true), Detached));

        Assert.Equal(
            new[] { (UdevEdgeKind.Disappeared, Detached), (UdevEdgeKind.Appeared, Attached), (UdevEdgeKind.Activated, Attached) },
            Edges(step));
    }

    [Fact]
    public void Move_BackAndForth_EndsHoldingOnlyWhereItLanded()
    {
        var there = UdevDispatch.Step(Holding(Port(Detached)), new("move", Attached, Port(Attached), Detached));
        var back = UdevDispatch.Step(there.Devices, new("move", Detached, Port(Detached), Attached));

        Assert.Equal(new[] { (UdevEdgeKind.Disappeared, Attached), (UdevEdgeKind.Appeared, Detached) }, Edges(back));
        Assert.Equal(new DeviceId[] { Detached }, back.Devices.Keys);
    }

    [Fact]
    public void Move_FromAPathNeverSeen_StillReportsTheOldIdLeaving()
    {
        var step = UdevDispatch.Step(None, new("move", Attached, Port(Attached), Detached));

        Assert.Equal(new[] { (UdevEdgeKind.Disappeared, Detached), (UdevEdgeKind.Appeared, Attached) }, Edges(step));
        Assert.Null(step.Edges[0].Device.Name);
    }

    [Fact]
    public void Move_ToADeviceThatMapsToNone_OnlyLeaves()
    {
        var step = UdevDispatch.Step(Holding(Port(Detached)), new("move", Attached, Device: null, Detached));

        Assert.Equal(new[] { (UdevEdgeKind.Disappeared, Detached) }, Edges(step));
        Assert.Empty(step.Devices);
    }

    [Fact]
    public void Move_WithoutAnOldPath_IsAnArrival()
    {
        var step = UdevDispatch.Step(None, new("move", Attached, Port(Attached)));

        Assert.Equal(new[] { (UdevEdgeKind.Appeared, Attached) }, Edges(step));
    }

    [Theory]
    [InlineData("/devices/virtual/net/pmove0", "/sys/devices/virtual/net/pmove0")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void OldSyspath_IsSysFollowedByDevpathOld(string? devpathOld, string? expected) =>
        Assert.Equal(expected, UdevDispatch.OldSyspath(devpathOld));

    // ── The other actions keep their edges ─────────────────────────────

    [Fact]
    public void Add_OfAnActiveDevice_AppearsThenActivates()
    {
        var step = UdevDispatch.Step(None, new("add", Detached, Port(Detached, active: true)));

        Assert.Equal(new[] { (UdevEdgeKind.Appeared, Detached), (UdevEdgeKind.Activated, Detached) }, Edges(step));
        Assert.Contains((DeviceId)Detached, step.Devices.Keys);
    }

    [Fact]
    public void Remove_OfAHeldDevice_ReportsWhatWasHeld_AndDropsIt()
    {
        var step = UdevDispatch.Step(Holding(Port(Detached)), new("remove", Detached));

        Assert.Equal("rfcomm0", Assert.Single(step.Edges).Device.Name);
        Assert.Empty(step.Devices);
    }

    [Fact]
    public void BindAndUnbind_ActivateAndDeactivate()
    {
        var bound = UdevDispatch.Step(None, new("bind", Detached, Port(Detached, active: true)));
        var unbound = UdevDispatch.Step(bound.Devices, new("unbind", Detached));

        Assert.Equal(new[] { (UdevEdgeKind.Activated, Detached) }, Edges(bound));
        Assert.Equal(new[] { (UdevEdgeKind.Deactivated, Detached) }, Edges(unbound));
        Assert.Contains((DeviceId)Detached, unbound.Devices.Keys);
    }

    [Fact]
    public void Change_ThatDeactivates_RaisesDeactivatedThenPropertyChanged()
    {
        var step = UdevDispatch.Step(Holding(Port(Detached, active: true)), new("change", Detached, Port(Detached)));

        Assert.Equal(new[] { (UdevEdgeKind.Deactivated, Detached), (UdevEdgeKind.PropertyChanged, Detached) }, Edges(step));
        Assert.True(step.Edges[1].Previous!.IsActive);
    }

    [Fact]
    public void Change_OfADeviceNeverSeen_IsHeldSilently()
    {
        var step = UdevDispatch.Step(None, new("change", Detached, Port(Detached)));

        Assert.Empty(step.Edges);
        Assert.Contains((DeviceId)Detached, step.Devices.Keys);
    }

    [Fact]
    public void AnUnknownAction_ChangesNothing()
    {
        var held = Holding(Port(Detached));
        var step = UdevDispatch.Step(held, new("online", Detached, Port(Detached)));

        Assert.Empty(step.Edges);
        Assert.Same(held, step.Devices);
    }

    [Theory]
    [InlineData("add", true)]
    [InlineData("bind", true)]
    [InlineData("change", true)]
    [InlineData("move", true)]
    [InlineData("remove", false)]
    [InlineData("unbind", false)]
    public void NeedsDevice_OnlyForActionsThatReadTheDevice(string action, bool expected) =>
        Assert.Equal(expected, UdevDispatch.NeedsDevice(action));
}

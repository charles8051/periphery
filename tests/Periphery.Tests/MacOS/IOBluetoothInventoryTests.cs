using System.Collections.Immutable;
using System.Net.NetworkInformation;
using Microsoft.Extensions.Logging;
using Periphery.MacOS.Bluetooth;
using Periphery.MacOS.Bluetooth.Core;
using Periphery.Tests.Linux;

namespace Periphery.Tests.MacOS;

/// <summary>
/// ADR-0093 as pure functions, and the source's rule that IOBluetooth is asked only with permission.
/// The mouse is shaped like the LE mouse a MacBook Air reported; its address is synthetic.
/// </summary>
public class IOBluetoothInventoryTests
{
    private const string MouseAddress = "c1-d2-e3-f4-a5-b6";
    private const string MouseId = "iobluetooth:C1:D2:E3:F4:A5:B6";
    private static readonly IOBluetoothBond Mouse = new(MouseAddress, "iClever Mouse 5.0", Connected: false, ClassOfDevice: 0);

    private static readonly ImmutableDictionary<DeviceId, DeviceInfo> Nothing = ImmutableDictionary<DeviceId, DeviceInfo>.Empty;

    // ── D3: the mapping ────────────────────────────────────────────────

    [Fact]
    public void Bond_MapsToABluetoothDevice_KeyedByItsAddress()
    {
        var device = IOBluetoothInventory.ToDeviceInfo(Mouse with { Connected = true })!;

        Assert.Equal(MouseId, device.Id.Value);
        Assert.Equal("iClever Mouse 5.0", device.Name);
        Assert.Equal(DeviceCategory.Bluetooth, device.Category);
        Assert.Equal(BusType.Bluetooth, device.BusType);
        Assert.Equal(DeviceStatus.OK, device.Status);
        Assert.True(device.IsActive);
        Assert.Equal(PhysicalAddress.Parse("C1-D2-E3-F4-A5-B6"), device.MacAddress);
    }

    [Theory]
    [InlineData(0u, BluetoothTransports.None)]          // an LE bond has no class of device
    [InlineData(0x002540u, BluetoothTransports.BrEdr)]  // keyboard
    public void Transports_AreBrEdr_OnlyWithAClassOfDevice(uint classOfDevice, BluetoothTransports expected) =>
        Assert.Equal(expected, IOBluetoothInventory.ToDeviceInfo(Mouse with { ClassOfDevice = classOfDevice })!.BluetoothTransports);

    [Fact]
    public void Bond_WithAnUnparsableAddress_IsLeftOut()
    {
        Assert.Null(IOBluetoothInventory.ToDeviceInfo(Mouse with { Address = "not-an-address" }));
        Assert.Empty(IOBluetoothInventory.Map([Mouse with { Address = "" }]));
    }

    [Fact]
    public void Map_KeepsOneDevicePerAddress_AndNullsAnEmptyName()
    {
        var devices = IOBluetoothInventory.Map([Mouse with { Name = "" }, Mouse with { Address = "C1:D2:E3:F4:A5:B6" }]);

        Assert.Null(Assert.Single(devices).Name);
    }

    // ── D1, D2: when to ask ────────────────────────────────────────────

    [Theory]
    [InlineData(null, true)]
    [InlineData(DeviceCategory.All, true)]
    [InlineData(DeviceCategory.Bluetooth, true)]
    [InlineData(DeviceCategory.Usb, false)]
    public void ShouldQuery_ByCategory(DeviceCategory? category, bool expected)
    {
        var filter = new DeviceFilter();
        if (category is { } c)
            filter.OfCategory(c);
        Assert.Equal(expected, IOBluetoothInventory.ShouldQuery(filter));
    }

    [Fact]
    public void ShouldQuery_NotForAUsbId() =>
        Assert.False(IOBluetoothInventory.ShouldQuery(new DeviceFilter().OfCategory(DeviceCategory.Bluetooth).WithUsbId("10C4")));

    [Theory]
    [InlineData(3, false)]  // AllowedAlways
    [InlineData(0, true)]   // NotDetermined
    [InlineData(2, true)]   // Denied
    [InlineData(1, true)]   // Restricted
    [InlineData(9, true)]   // a value this build does not know
    public void Unavailable_UnlessAllowedAlways(int authorization, bool unavailable) =>
        Assert.Equal(unavailable, IOBluetoothInventory.Unavailable((BluetoothAuthorization)authorization) is not null);

    [Fact]
    public void Source_WithoutPermission_NeverAsksIOBluetooth_AndLogsOnce()
    {
        int asked = 0;
        var logger = new RecordingLogger();
        var source = new IOBluetoothDeviceSource(() => BluetoothAuthorization.NotDetermined, () => { asked++; return [Mouse]; }, () => [], logger);

        Assert.Null(source.Snapshot());
        Assert.Empty(source.Enumerate(CancellationToken.None));

        Assert.Equal(0, asked);
        Assert.Contains("no Bluetooth permission", Assert.Single(logger.Entries).Message);
    }

    [Fact]
    public void Source_WhenIOBluetoothDoesNotLoad_IsUnavailable()
    {
        var logger = new RecordingLogger();
        var source = new IOBluetoothDeviceSource(() => null, () => [Mouse], () => [], logger);

        Assert.Null(source.Snapshot());
        Assert.Equal(LogLevel.Warning, Assert.Single(logger.Entries).Level);
    }

    [Fact]
    public void Source_WithPermission_MapsTheBonds()
    {
        var source = new IOBluetoothDeviceSource(() => BluetoothAuthorization.AllowedAlways, () => [Mouse], () => [], new RecordingLogger());

        Assert.Equal(MouseId, Assert.Single(source.Snapshot()!.Value).Id.Value);
    }

    // ── D3 amendment: transports from the HID node ─────────────────────

    [Theory]
    [InlineData("Bluetooth Low Energy", BluetoothTransports.LowEnergy)]
    [InlineData("Bluetooth", BluetoothTransports.BrEdr)]
    [InlineData("USB", BluetoothTransports.None)]
    [InlineData("SPI", BluetoothTransports.None)]
    public void TransportOf_ReadsTheHidTransport(string transport, BluetoothTransports expected) =>
        Assert.Equal(expected, IOBluetoothInventory.TransportOf(transport));

    [Fact]
    public void Learn_KeysByAddress_InEitherForm_AndUnitesTransports()
    {
        var known = IOBluetoothInventory.Learn(
            ImmutableDictionary<BluetoothAddress, BluetoothTransports>.Empty,
            [new HidLink("C1:D2:E3:F4:A5:B6", "Bluetooth Low Energy"), new HidLink(MouseAddress, "Bluetooth"),
             new HidLink("not-an-address", "Bluetooth"), new HidLink("A0:B1:C2:D3:E4:F5", "USB")]);

        Assert.Equal(BluetoothTransports.LowEnergy | BluetoothTransports.BrEdr, Assert.Single(known).Value);
    }

    [Fact]
    public void Bond_TakesTheTransportsItsHidNodeShowed()
    {
        var known = IOBluetoothInventory.Learn(
            ImmutableDictionary<BluetoothAddress, BluetoothTransports>.Empty, [new HidLink("C1:D2:E3:F4:A5:B6", "Bluetooth Low Energy")]);

        Assert.Equal(BluetoothTransports.LowEnergy, IOBluetoothInventory.ToDeviceInfo(Mouse, known)!.BluetoothTransports);
        Assert.Equal(BluetoothTransports.BrEdr | BluetoothTransports.LowEnergy,
            IOBluetoothInventory.ToDeviceInfo(Mouse with { ClassOfDevice = 0x002580 }, known)!.BluetoothTransports);
    }

    [Fact]
    public void Source_KeepsATransport_AfterTheHidNodeGoes()
    {
        var links = new Queue<ImmutableArray<HidLink>>([[new HidLink(MouseAddress, "Bluetooth Low Energy")], []]);
        var source = new IOBluetoothDeviceSource(
            () => BluetoothAuthorization.AllowedAlways, () => [Mouse], () => links.Dequeue(), new RecordingLogger());

        Assert.Equal(BluetoothTransports.LowEnergy, Assert.Single(source.Snapshot()!.Value).BluetoothTransports);
        Assert.Equal(BluetoothTransports.LowEnergy, Assert.Single(source.Snapshot()!.Value).BluetoothTransports);
    }

    // ── D4: the watch step ─────────────────────────────────────────────

    private static ImmutableArray<DeviceInfo> Snapshot(params IOBluetoothBond[] bonds) => IOBluetoothInventory.Map(bonds);

    private static (IOBluetoothEdgeKind, string)[] Edges(ImmutableArray<IOBluetoothEdge> edges) =>
        [.. edges.Select(e => (e.Kind, e.Device.Id.Value))];

    [Fact]
    public void Step_ANewBond_Appears_AndActivatesIfConnected()
    {
        var (held, edges) = IOBluetoothInventory.Step(Nothing, Snapshot(Mouse with { Connected = true }));

        Assert.Equal(new[] { (IOBluetoothEdgeKind.Appeared, MouseId), (IOBluetoothEdgeKind.Activated, MouseId) }, Edges(edges));
        Assert.Single(held);
    }

    [Fact]
    public void Step_AConnection_IsActivatedThenPropertyChanged()
    {
        var (held, _) = IOBluetoothInventory.Step(Nothing, Snapshot(Mouse));
        var (_, edges) = IOBluetoothInventory.Step(held, Snapshot(Mouse with { Connected = true }));

        Assert.Equal(new[] { (IOBluetoothEdgeKind.Activated, MouseId), (IOBluetoothEdgeKind.PropertyChanged, MouseId) }, Edges(edges));
        Assert.False(edges[1].Previous!.IsActive);
    }

    [Fact]
    public void Step_ARename_IsOnlyAPropertyChange()
    {
        var (held, _) = IOBluetoothInventory.Step(Nothing, Snapshot(Mouse));
        var (_, edges) = IOBluetoothInventory.Step(held, Snapshot(Mouse with { Name = "Desk mouse" }));

        Assert.Equal(new[] { (IOBluetoothEdgeKind.PropertyChanged, MouseId) }, Edges(edges));
    }

    [Fact]
    public void Step_AnUnchangedSnapshot_RaisesNothing()
    {
        var (held, _) = IOBluetoothInventory.Step(Nothing, Snapshot(Mouse));

        Assert.Empty(IOBluetoothInventory.Step(held, Snapshot(Mouse)).Edges);
    }

    [Fact]
    public void Step_AnUnpairedBond_Disappears()
    {
        var (held, _) = IOBluetoothInventory.Step(Nothing, Snapshot(Mouse));
        var (after, edges) = IOBluetoothInventory.Step(held, Snapshot());

        Assert.Equal(new[] { (IOBluetoothEdgeKind.Disappeared, MouseId) }, Edges(edges));
        Assert.Empty(after);
    }

    [Fact]
    public void Step_AnUnavailableSnapshot_KeepsWhatIsHeld()
    {
        var (held, _) = IOBluetoothInventory.Step(Nothing, Snapshot(Mouse));
        var (after, edges) = IOBluetoothInventory.Step(held, null);

        Assert.Empty(edges);
        Assert.Same(held, after);
    }
}

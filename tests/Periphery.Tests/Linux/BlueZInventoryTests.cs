using System.Collections.Immutable;
using System.Net.NetworkInformation;
using Periphery.Linux.BlueZ.Core;
using Periphery.Linux.DBus.Core;

namespace Periphery.Tests.Linux;

/// <summary>
/// ADR-0091 D1, D3 and D4 as pure functions: which filters reach BlueZ, how an error is treated, and
/// how a <c>GetManagedObjects</c> reply maps to devices.
/// </summary>
public class BlueZInventoryTests
{
    private const string CentralBond = "bluez:00:AA:01:00:00:00/00:AA:01:01:00:01";
    private const string PeripheralBond = "bluez:00:AA:01:01:00:01/00:AA:01:00:00:00";

    // ── Captured replies ───────────────────────────────────────────────

    [Fact]
    public void CapturedReply_Disconnected_YieldsBothBonds()
    {
        var devices = BlueZInventory.Map(Body(BlueZFixtures.ManagedObjectsDisconnected));

        Assert.Equal(new[] { CentralBond, PeripheralBond }, devices.Select(d => d.Id.Value));

        var central = devices[0];
        Assert.Equal("periphery-bench-01", central.Name);
        Assert.Equal(PhysicalAddress.Parse("00-AA-01-01-00-01"), central.MacAddress);
        Assert.Equal("/org/bluez/hci0/dev_00_AA_01_01_00_01", central.LocationPath);
        Assert.Equal(DeviceCategory.Bluetooth, central.Category);
        Assert.Equal(BusType.Bluetooth, central.BusType);
        Assert.Equal(DeviceStatus.OK, central.Status);
        Assert.False(central.IsActive);
        Assert.Null(central.ParentId);

        Assert.Equal("periphery-bench-01 #1", devices[1].Name);
        Assert.False(devices[1].IsActive);
    }

    [Fact]
    public void CapturedReply_Connected_MarksBothSidesActive()
    {
        var devices = BlueZInventory.Map(Body(BlueZFixtures.ManagedObjectsConnected));

        Assert.Equal(new[] { CentralBond, PeripheralBond }, devices.Select(d => d.Id.Value));
        Assert.All(devices, d => Assert.True(d.IsActive));
    }

    [Fact]
    public void CapturedReply_AfterRestart_YieldsBothBonds()
    {
        var devices = BlueZInventory.Map(Body(BlueZFixtures.ManagedObjectsAfterRestart));

        Assert.Equal(new[] { CentralBond, PeripheralBond }, devices.Select(d => d.Id.Value));
    }

    // ── D1: what counts as a bond ──────────────────────────────────────

    [Theory]
    [InlineData(true, true, true)]    // BlueZ 5.65+: Bonded decides
    [InlineData(false, true, false)]  // paired, keys not stored: not a bond
    [InlineData(null, true, true)]    // before 5.65: Paired decides
    [InlineData(null, false, false)]  // discovered, never paired
    public void Bonded_IsBondedWhereItExists_ElsePaired(bool? bonded, bool paired, bool expected)
    {
        var properties = new List<(string, DBusValue)> { ("Paired", new DBusBoolean(paired)) };
        if (bonded is { } value)
            properties.Add(("Bonded", new DBusBoolean(value)));

        var devices = BlueZInventory.Map(Managed(Adapter0, Device("dev_11", "11:22:33:44:55:66", [.. properties])));

        Assert.Equal(expected, devices.Length == 1);
    }

    // ── D4: fields ─────────────────────────────────────────────────────

    [Fact]
    public void Name_IsNullForBlueZsAddressFallback()
    {
        var devices = BlueZInventory.Map(Managed(Adapter0,
            Device("dev_11", "11:22:33:44:55:66", ("Bonded", new DBusBoolean(true)), ("Alias", S("11-22-33-44-55-66")))));

        Assert.Null(Assert.Single(devices).Name);
    }

    [Fact]
    public void Name_IsTheAlias_WhenANameWasRead()
    {
        var devices = BlueZInventory.Map(Managed(Adapter0,
            Device("dev_11", "11:22:33:44:55:66", ("Bonded", new DBusBoolean(true)), ("Name", S("Sensor")), ("Alias", S("Kitchen sensor")))));

        Assert.Equal("Kitchen sensor", Assert.Single(devices).Name);
    }

    [Fact]
    public void Id_UsesUppercaseColonForm_WhateverCaseBlueZSent()
    {
        var devices = BlueZInventory.Map(Managed(Adapter0,
            Device("dev_aa", "aa:bb:cc:dd:ee:ff", ("Bonded", new DBusBoolean(true)))));

        Assert.Equal("bluez:00:AA:01:00:00:00/AA:BB:CC:DD:EE:FF", Assert.Single(devices).Id.Value);
    }

    [Fact]
    public void DeviceWithoutItsAdapter_OrWithABadAddress_IsLeftOut()
    {
        var devices = BlueZInventory.Map(Managed(
            Device("dev_11", "11:22:33:44:55:66", ("Bonded", new DBusBoolean(true))),
            Adapter0,
            Device("dev_bad", "not an address", ("Bonded", new DBusBoolean(true))),
            Object("/org/bluez/hci0/dev_22", Interface(BlueZInventory.DeviceInterface,
                ("Address", S("22:22:22:22:22:22")), ("Adapter", new DBusString('o', "/org/bluez/hci9")), ("Bonded", new DBusBoolean(true))))));

        Assert.Equal(new[] { "bluez:00:AA:01:00:00:00/11:22:33:44:55:66" }, devices.Select(d => d.Id.Value));
    }

    [Fact]
    public void TwoObjectsForOneBond_TheConnectedOneWins()
    {
        // BlueZ keeps a privacy peer's older object when a new pairing resolves to the same identity.
        var devices = BlueZInventory.Map(Managed(Adapter0,
            Device("dev_11", "11:22:33:44:55:66", ("Bonded", new DBusBoolean(true)), ("Connected", new DBusBoolean(false))),
            Device("dev_7A", "11:22:33:44:55:66", ("Bonded", new DBusBoolean(true)), ("Connected", new DBusBoolean(true)))));

        var device = Assert.Single(devices);
        Assert.True(device.IsActive);
        Assert.Equal("/org/bluez/hci0/dev_7A", device.LocationPath);
    }

    [Fact]
    public void TwoObjectsForOneBond_NeitherConnected_TheLowerPathWins()
    {
        var devices = BlueZInventory.Map(Managed(Adapter0,
            Device("dev_7A", "11:22:33:44:55:66", ("Bonded", new DBusBoolean(true))),
            Device("dev_11", "11:22:33:44:55:66", ("Bonded", new DBusBoolean(true)))));

        Assert.Equal("/org/bluez/hci0/dev_11", Assert.Single(devices).LocationPath);
    }

    [Fact]
    public void NotAManagedObjectsValue_YieldsNothing() =>
        Assert.Empty(BlueZInventory.Map(new DBusString('s', "not objects")));

    // ── D1: scope ──────────────────────────────────────────────────────

    [Fact]
    public void Scope_ReachesBlueZ_OnlyWhenTheFilterCouldMatchABlueZDevice()
    {
        Assert.True(BlueZInventory.ShouldQuery(new DeviceFilter()));
        Assert.True(BlueZInventory.ShouldQuery(new DeviceFilter().OfCategory(DeviceCategory.All)));
        Assert.True(BlueZInventory.ShouldQuery(new DeviceFilter().OfCategory(DeviceCategory.Bluetooth)));
        Assert.True(BlueZInventory.ShouldQuery(new DeviceFilter().OfCategory(DeviceCategory.Bluetooth).WithName("Sensor")));

        Assert.False(BlueZInventory.ShouldQuery(new DeviceFilter().OfCategory(DeviceCategory.Usb)));
        Assert.False(BlueZInventory.ShouldQuery(new DeviceFilter().WithUsbId("046D", "C52B")));
        Assert.False(BlueZInventory.ShouldQuery(new DeviceFilter().OfCategory(DeviceCategory.Bluetooth).WithUsbId("046D")));
    }

    // ── D3: errors ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("org.freedesktop.DBus.Error.NameHasNoOwner", (int)BlueZFailureKind.Absent)]
    [InlineData("org.freedesktop.DBus.Error.ServiceUnknown", (int)BlueZFailureKind.Absent)]
    [InlineData("org.freedesktop.DBus.Error.AccessDenied", (int)BlueZFailureKind.AccessDenied)]
    [InlineData("org.freedesktop.DBus.Error.NoReply", (int)BlueZFailureKind.Error)]
    [InlineData("org.bluez.Error.Failed", (int)BlueZFailureKind.Error)]
    public void Errors_AreClassifiedByName(string errorName, int expected) =>
        Assert.Equal((BlueZFailureKind)expected, BlueZInventory.ClassifyError(errorName));

    // ── Helpers ────────────────────────────────────────────────────────

    private static readonly DBusDictEntry Adapter0 =
        Object("/org/bluez/hci0", Interface(BlueZInventory.AdapterInterface, ("Address", S("00:AA:01:00:00:00"))));

    private static DBusValue Body(string fixture) => Assert.Single(BlueZFixtures.Message(fixture).Body);

    private static DBusString S(string value) => new('s', value);

    private static DBusValue Managed(params DBusDictEntry[] objects) => new DBusArray("{oa{sa{sv}}}", [.. objects]);

    private static DBusDictEntry Object(string path, params DBusDictEntry[] interfaces) =>
        new(new DBusString('o', path), new DBusArray("{sa{sv}}", [.. interfaces]));

    private static DBusDictEntry Interface(string name, params (string Key, DBusValue Value)[] properties) =>
        new(S(name), new DBusArray("{sv}", properties
            .Select(p => (DBusValue)new DBusDictEntry(S(p.Key), new DBusVariant(SignatureOf(p.Value), p.Value)))
            .ToImmutableArray()));

    private static DBusDictEntry Device(string node, string address, params (string Key, DBusValue Value)[] properties) =>
        Object($"/org/bluez/hci0/{node}", Interface(BlueZInventory.DeviceInterface,
            [("Address", S(address)), ("Adapter", new DBusString('o', "/org/bluez/hci0")), .. properties]));

    private static string SignatureOf(DBusValue value) => value switch
    {
        DBusBoolean => "b",
        DBusString s => s.Code.ToString(),
        _ => throw new ArgumentException(value.GetType().Name),
    };
}

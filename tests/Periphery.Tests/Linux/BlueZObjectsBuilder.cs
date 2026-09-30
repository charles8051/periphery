using System.Collections.Immutable;
using Periphery.Linux.BlueZ.Core;
using Periphery.Linux.DBus.Core;

namespace Periphery.Tests.Linux;

/// <summary>
/// Builds BlueZ's object tree and signals by hand, for what the captured traffic does not cover.
/// Everything sits on adapter hci0, <c>00:AA:01:00:00:00</c>.
/// </summary>
internal static class BlueZObjectsBuilder
{
    public const string Owner = ":1.3769";

    public static readonly DBusDictEntry Adapter0 =
        Object("/org/bluez/hci0", Interface(BlueZInventory.AdapterInterface, ("Address", S("00:AA:01:00:00:00"))));

    public static DBusValue Body(string fixture) => Assert.Single(BlueZFixtures.Message(fixture).Body);

    public static DBusString S(string value) => new('s', value);

    public static DBusBoolean B(bool value) => new(value);

    public static DBusValue Managed(params DBusDictEntry[] objects) => new DBusArray("{oa{sa{sv}}}", [.. objects]);

    public static DBusDictEntry Object(string path, params DBusDictEntry[] interfaces) =>
        new(new DBusString('o', path), new DBusArray("{sa{sv}}", [.. interfaces]));

    public static DBusDictEntry Interface(string name, params (string Key, DBusValue Value)[] properties) =>
        new(S(name), Properties(properties));

    public static DBusArray Properties(params (string Key, DBusValue Value)[] properties) =>
        new("{sv}", properties
            .Select(p => (DBusValue)new DBusDictEntry(S(p.Key), new DBusVariant(SignatureOf(p.Value), p.Value)))
            .ToImmutableArray());

    public static DBusDictEntry Device(string node, string address, params (string Key, DBusValue Value)[] properties) =>
        Object($"/org/bluez/hci0/{node}", Interface(BlueZInventory.DeviceInterface,
            [("Address", S(address)), ("Adapter", new DBusString('o', "/org/bluez/hci0")), .. properties]));

    /// <summary>A reply to <c>GetManagedObjects</c> sent under <paramref name="replySerial"/>.</summary>
    public static DBusMessage Reply(uint replySerial, DBusValue objects, string sender = Owner) => new()
    {
        Type = DBusMessageType.MethodReturn,
        Serial = 900,
        ReplySerial = replySerial,
        Sender = sender,
        Signature = BlueZInventory.ManagedObjectsSignature,
        Body = [objects],
    };

    public static DBusMessage Error(uint replySerial, string name, string sender) => new()
    {
        Type = DBusMessageType.Error,
        Serial = 901,
        ReplySerial = replySerial,
        ErrorName = name,
        Sender = sender,
        Signature = "s",
        Body = [S("from the test")],
    };

    public static DBusMessage PropertiesChanged(string node, (string Key, DBusValue Value)[] changed, string[]? invalidated = null, string sender = Owner) => new()
    {
        Type = DBusMessageType.Signal,
        Serial = 902,
        Sender = sender,
        Path = $"/org/bluez/hci0/{node}",
        Interface = "org.freedesktop.DBus.Properties",
        Member = "PropertiesChanged",
        Signature = "sa{sv}as",
        Body = [S(BlueZInventory.DeviceInterface), Properties(changed), new DBusArray("s", [.. (invalidated ?? []).Select(i => (DBusValue)S(i))])],
    };

    public static DBusMessage InterfacesAdded(DBusDictEntry obj, string sender = Owner) => new()
    {
        Type = DBusMessageType.Signal,
        Serial = 903,
        Sender = sender,
        Path = "/",
        Interface = BlueZInventory.ObjectManagerInterface,
        Member = "InterfacesAdded",
        Signature = "oa{sa{sv}}",
        Body = [obj.Key, obj.Value],
    };

    public static DBusMessage InterfacesRemoved(string path, string sender = Owner, params string[] interfaces) => new()
    {
        Type = DBusMessageType.Signal,
        Serial = 904,
        Sender = sender,
        Path = "/",
        Interface = BlueZInventory.ObjectManagerInterface,
        Member = "InterfacesRemoved",
        Signature = "oas",
        Body = [new DBusString('o', path), new DBusArray("s", [.. interfaces.Select(i => (DBusValue)S(i))])],
    };

    public static DBusMessage OwnerChanged(string oldOwner, string newOwner, string sender = "org.freedesktop.DBus", string name = "org.bluez") => new()
    {
        Type = DBusMessageType.Signal,
        Serial = 905,
        Sender = sender,
        Path = "/org/freedesktop/DBus",
        Interface = "org.freedesktop.DBus",
        Member = "NameOwnerChanged",
        Signature = "sss",
        Body = [S(name), S(oldOwner), S(newOwner)],
    };

    /// <summary>A <c>UUIDs</c> value: an array of strings.</summary>
    public static DBusArray Uuids(params string[] uuids) => new("s", [.. uuids.Select(u => (DBusValue)S(u))]);

    private static string SignatureOf(DBusValue value) => value switch
    {
        DBusBoolean => "b",
        DBusString s => s.Code.ToString(),
        DBusInteger i => i.Code.ToString(),
        DBusArray a => "a" + a.ElementSignature,
        _ => throw new ArgumentException(value.GetType().Name),
    };
}

// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Immutable;

namespace Periphery.Linux.DBus.Core;

/// <summary>The message types the specification defines. Others decode and are ignored.</summary>
internal enum DBusMessageType : byte
{
    Invalid = 0,
    MethodCall = 1,
    MethodReturn = 2,
    Error = 3,
    Signal = 4,
}

/// <summary>Header flags.</summary>
[Flags]
internal enum DBusMessageFlags : byte
{
    None = 0,
    NoReplyExpected = 0x1,

    /// <summary>The bus must not start a service to receive this message (ADR-0091 D2).</summary>
    NoAutoStart = 0x2,

    AllowInteractiveAuthorization = 0x4,
}

/// <summary>
/// One decoded D-Bus message. <see cref="Body"/> holds one value per complete type in
/// <see cref="Signature"/>.
/// </summary>
internal sealed record DBusMessage
{
    public required DBusMessageType Type { get; init; }
    public DBusMessageFlags Flags { get; init; }

    /// <summary>The sender's serial. Never zero on the wire.</summary>
    public uint Serial { get; init; }

    public string? Path { get; init; }
    public string? Interface { get; init; }
    public string? Member { get; init; }
    public string? ErrorName { get; init; }
    public uint? ReplySerial { get; init; }
    public string? Destination { get; init; }
    public string? Sender { get; init; }
    public string Signature { get; init; } = "";
    public ImmutableArray<DBusValue> Body { get; init; } = ImmutableArray<DBusValue>.Empty;

    /// <summary>A method call that must not start a service, with string arguments.</summary>
    internal static DBusMessage Call(string destination, string path, string @interface, string member, params string[] args) => new()
    {
        Type = DBusMessageType.MethodCall,
        Flags = DBusMessageFlags.NoAutoStart,
        Destination = destination,
        Path = path,
        Interface = @interface,
        Member = member,
        Signature = new string('s', args.Length),
        Body = args.Select(a => (DBusValue)new DBusString('s', a)).ToImmutableArray(),
    };
}

// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Linux.DBus.Core;

/// <summary>
/// Bytes that break the D-Bus wire format, or a handshake the client cannot complete. The BlueZ leg
/// turns it into an empty answer (ADR-0091 D3).
/// </summary>
internal sealed class DBusProtocolException : Exception
{
    public DBusProtocolException(string message) : base(message) { }
}

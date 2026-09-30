// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Linux.DBus.Core;

/// <summary>Where the client is in the SASL handshake.</summary>
internal enum DBusAuthState
{
    /// <summary>The greeting is sent. The server answers with an empty challenge or with OK.</summary>
    AwaitingChallenge,

    /// <summary>The empty response is sent. The server answers with OK.</summary>
    AwaitingOk,

    /// <summary>BEGIN is sent. The connection carries messages from here on.</summary>
    Authenticated,

    /// <summary>The server refused, or said something the client does not expect.</summary>
    Rejected,
}

/// <summary>The client's next state, and the line it sends, if any.</summary>
internal readonly record struct DBusAuthStep(DBusAuthState State, string? Send);

/// <summary>
/// The client side of the handshake, as a pure step over server lines (ADR-0091 D2). The client
/// offers <c>EXTERNAL</c> with no identity and answers the server's challenge with an empty
/// response, so dbus-daemon authenticates the socket's credentials and the client needs no uid.
/// </summary>
internal static class DBusAuth
{
    /// <summary>What the client sends first: the credentials byte, then the mechanism.</summary>
    internal const string Greeting = "\0AUTH EXTERNAL\r\n";

    /// <summary>Advances the handshake by one server line, without its trailing CRLF.</summary>
    internal static DBusAuthStep Next(DBusAuthState state, string line) => state switch
    {
        DBusAuthState.AwaitingChallenge when IsCommand(line, "DATA") => new(DBusAuthState.AwaitingOk, "DATA\r\n"),
        DBusAuthState.AwaitingChallenge or DBusAuthState.AwaitingOk when IsCommand(line, "OK") =>
            new(DBusAuthState.Authenticated, "BEGIN\r\n"),
        _ => new(DBusAuthState.Rejected, null),
    };

    private static bool IsCommand(string line, string command) =>
        line.Equals(command, StringComparison.Ordinal)
        || line.StartsWith(command + " ", StringComparison.Ordinal);
}

// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Cli.Serial.Core;

/// <summary>
/// Turns session-host status changes into the status lines a terminal prints.
/// Repeats are suppressed, so a port that keeps failing to open reports once
/// rather than on every retry.
/// </summary>
/// <param name="Port">The port name to report when a status carries no device.</param>
/// <param name="Settings">The line settings, for the connected line.</param>
/// <param name="EverConnected">Whether a session has been active, which turns "connected" into "reconnected".</param>
/// <param name="LastLine">The last line printed, for repeat suppression.</param>
internal sealed record StatusNarrator(
    string Port,
    SerialLineSettings Settings,
    bool EverConnected = false,
    string? LastLine = null)
{
    internal (StatusNarrator Next, string? Line) Next<TSession>(HostStatus<TSession> status)
        where TSession : class
    {
        bool everConnected = EverConnected || status is SessionActive<TSession>;
        string? line = status switch
        {
            SessionActive<TSession> active =>
                $"{(EverConnected ? "Reconnected to" : "Connected to")} {Describe(active.Device)} at {Settings.Describe()}."
                + (EverConnected ? "" : " Ctrl+] to quit."),
            DeviceAbsent<TSession> => EverConnected
                ? $"Disconnected. Waiting for {Port} to return."
                : $"Waiting for {Port}.",
            SessionUnavailable<TSession> unavailable =>
                $"Cannot open {PortOf(unavailable.Device)}: {Reason(unavailable.LastError)} Retrying.",
            SessionGaveUp<TSession> gaveUp =>
                $"Gave up opening {Port}: {Reason(gaveUp.LastError)}",
            _ => null,
        };

        // Device names and error text are device- and driver-reported.
        if (line is not null) line = TerminalText.Printable(line);

        if (line is null || line == LastLine)
            return (this with { EverConnected = everConnected }, null);
        return (this with { EverConnected = everConnected, LastLine = line }, line);
    }

    private string PortOf(DeviceInfo device) => device.PortName?.Value ?? Port;

    private string Describe(DeviceInfo device)
    {
        string port = PortOf(device);
        return device.Name is { } name && !name.Contains(port, StringComparison.OrdinalIgnoreCase)
            ? $"{port} ({name})"
            : device.Name ?? port;
    }

    private static string Reason(Exception? error)
    {
        if (error is null) return "unknown error.";
        string message = error.Message.TrimEnd();
        return message.EndsWith('.') ? message : message + ".";
    }
}

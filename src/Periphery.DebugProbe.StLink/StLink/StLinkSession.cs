// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0
//
// The open sequence (mode to idle, version, firmware floor, voltage, SWD entry, AP init) and the
// read-then-GETLASTRWSTATUS2 pattern follow probe-rs (probe-rs/src/probe/stlink/mod.rs,
// MIT OR Apache-2.0, Copyright (c) 2019 probe-rs). See THIRD-PARTY-NOTICES.md.

namespace Periphery.DebugProbe.StLink;

/// <summary>
/// The ST-Link shell: owns the open sequence and turns the memory seam into ST-Link commands.
/// A WAIT status is retried at once; the next USB round trip is the back-off.
/// </summary>
internal sealed class StLinkSession : ITargetMemory
{
    private const int MaxWaitRetries = 16;

    private readonly IStLinkTransport _transport;

    private StLinkSession(IStLinkTransport transport, StLinkVersion version, double targetVoltage)
    {
        _transport = transport;
        Version = version;
        TargetVoltage = targetVoltage;
    }

    public StLinkVersion Version { get; }

    /// <summary>The target supply the probe measured at open, in volts.</summary>
    public double TargetVoltage { get; }

    /// <summary>
    /// Returns the probe to idle, checks its firmware against ADR-0094 D7, reads the target
    /// voltage, enters SWD, and opens access port 0.
    /// </summary>
    public static async Task<StLinkSession> OpenAsync(IStLinkTransport transport, CancellationToken ct)
    {
        var mode = StLinkProtocol.DecodeMode(
            await transport.ExchangeAsync(StLinkProtocol.GetCurrentMode(), default, StLinkProtocol.ModeReplyLength, ct)
                .ConfigureAwait(false));
        if (StLinkProtocol.ExitMode(mode) is { } exit)
            await transport.ExchangeAsync(exit, default, 0, ct).ConfigureAwait(false);

        var versionReply = await transport.ExchangeAsync(
            StLinkProtocol.GetVersion(), default, StLinkProtocol.VersionReplyLength, ct).ConfigureAwait(false);
        byte[] extended = [];
        if (((versionReply[0] >> 4) & 0x0F) >= 3)
            extended = await transport.ExchangeAsync(
                StLinkProtocol.GetVersionExtended(), default, StLinkProtocol.VersionExtendedReplyLength, ct).ConfigureAwait(false);
        var version = StLinkProtocol.DecodeVersion(versionReply, extended);

        if (StLinkProtocol.FirmwareFloorViolation(version) is { } violation)
            throw new StLinkException(violation);

        double voltage = StLinkProtocol.DecodeTargetVoltage(
            await transport.ExchangeAsync(StLinkProtocol.GetTargetVoltage(), default, StLinkProtocol.VoltageReplyLength, ct)
                .ConfigureAwait(false));

        var session = new StLinkSession(transport, version, voltage);
        await session.CommandAsync(StLinkProtocol.EnterSwd(), "enter SWD", ct).ConfigureAwait(false);
        if (StLinkProtocol.SupportsInitAp(version))
            await session.CommandAsync(StLinkProtocol.InitAp(0), "open access port 0", ct).ConfigureAwait(false);
        return session;
    }

    /// <summary>Returns the probe to idle so other tools can use it.</summary>
    public Task CloseAsync(CancellationToken ct) =>
        _transport.ExchangeAsync(StLinkProtocol.ExitMode(StLinkMode.Debug)!, default, 0, ct);

    public async Task<uint> ReadWordAsync(uint address, CancellationToken ct)
    {
        var reply = await ExchangeCheckedAsync(
            StLinkProtocol.ReadDebugReg(address), default, StLinkProtocol.ReadDebugRegReplyLength,
            $"read 0x{address:X8}", ct).ConfigureAwait(false);
        return StLinkProtocol.DecodeDebugRegValue(reply);
    }

    public Task WriteWordAsync(uint address, uint value, CancellationToken ct) =>
        CommandAsync(StLinkProtocol.WriteDebugReg(address, value), $"write 0x{address:X8}", ct);

    public async Task ReadAsync(uint address, Memory<byte> destination, CancellationToken ct)
    {
        int done = 0;
        while (done < destination.Length)
        {
            int length = Math.Min(StLinkProtocol.MaxRead32, destination.Length - done);
            uint at = address + (uint)done;
            for (int attempt = 0; ; attempt++)
            {
                var data = await _transport.ExchangeAsync(
                    StLinkProtocol.ReadMem32(at, length), default, length, ct).ConfigureAwait(false);
                var status = await LastRwStatusAsync(ct).ConfigureAwait(false);
                if (status == StLinkStatus.Ok)
                {
                    data.CopyTo(destination[done..]);
                    break;
                }
                ThrowUnlessWait(status, attempt, $"read {length} bytes at 0x{at:X8}");
            }
            done += length;
        }
    }

    public async Task WriteAsync(uint address, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        int done = 0;
        while (done < data.Length)
        {
            int length = Math.Min(StLinkProtocol.MaxWrite32, data.Length - done);
            uint at = address + (uint)done;
            for (int attempt = 0; ; attempt++)
            {
                await _transport.ExchangeAsync(
                    StLinkProtocol.WriteMem32(at, length), data.Slice(done, length), 0, ct).ConfigureAwait(false);
                var status = await LastRwStatusAsync(ct).ConfigureAwait(false);
                if (status == StLinkStatus.Ok)
                    break;
                ThrowUnlessWait(status, attempt, $"write {length} bytes at 0x{at:X8}");
            }
            done += length;
        }
    }

    private async Task<StLinkStatus> LastRwStatusAsync(CancellationToken ct) =>
        StLinkProtocol.DecodeStatus(await _transport.ExchangeAsync(
            StLinkProtocol.GetLastRwStatus2(), default, StLinkProtocol.LastRwStatusReplyLength, ct).ConfigureAwait(false));

    private async Task CommandAsync(byte[] command, string what, CancellationToken ct) =>
        await ExchangeCheckedAsync(command, default, StLinkProtocol.StatusReplyLength, what, ct).ConfigureAwait(false);

    private async Task<byte[]> ExchangeCheckedAsync(
        byte[] command, ReadOnlyMemory<byte> dataOut, int replyLength, string what, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            var reply = await _transport.ExchangeAsync(command, dataOut, replyLength, ct).ConfigureAwait(false);
            var status = StLinkProtocol.DecodeStatus(reply);
            if (status == StLinkStatus.Ok)
                return reply;
            ThrowUnlessWait(status, attempt, what);
        }
    }

    private static void ThrowUnlessWait(StLinkStatus status, int attempt, string what)
    {
        if (!StLinkProtocol.IsWait(status) || attempt >= MaxWaitRetries)
            throw new StLinkException($"ST-Link could not {what}", status);
    }
}

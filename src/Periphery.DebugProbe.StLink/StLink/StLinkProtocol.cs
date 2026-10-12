// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0
//
// The ST-Link command codes, response layouts, PID-to-endpoint table and firmware floors here are
// adapted from probe-rs (probe-rs/src/probe/stlink/, MIT OR Apache-2.0, Copyright (c) 2019 probe-rs).
// Changes: re-expressed as pure C# encoders and decoders. See THIRD-PARTY-NOTICES.md.

using System.Buffers.Binary;
using System.Collections.Frozen;

namespace Periphery.DebugProbe.StLink;

/// <summary>The status byte an ST-Link returns in the first byte of most replies.</summary>
internal enum StLinkStatus : byte
{
    UnknownError = 0x01,
    SpiError = 0x02,
    DmaError = 0x03,
    UnknownJtagChain = 0x04,
    NoDeviceConnected = 0x05,
    InternalError = 0x06,
    CommandWait = 0x07,
    CommandError = 0x08,
    GetIdcodeError = 0x09,
    AlignmentError = 0x0A,
    DebugPowerError = 0x0B,
    WriteError = 0x0C,
    WriteVerifyError = 0x0D,
    AlreadyOpenedInOtherMode = 0x0E,
    SwdApWait = 0x10,
    SwdApFault = 0x11,
    SwdApError = 0x12,
    SwdApParityError = 0x13,
    SwdDpWait = 0x14,
    SwdDpFault = 0x15,
    SwdDpError = 0x16,
    SwdDpParityError = 0x17,
    SwdApWdataError = 0x18,
    SwdApStickyError = 0x19,
    SwdApStickyOverrunError = 0x1A,
    BadApError = 0x1D,
    SwoNotAvailable = 0x20,
    Ok = 0x80,
    FrequencyNotSupported = 0x41,
    UnknownCommand = 0x42,
}

/// <summary>The mode an ST-Link reports from <c>GET_CURRENT_MODE</c>.</summary>
internal enum StLinkMode : byte
{
    Dfu = 0,
    MassStorage = 1,
    Debug = 2,
    Swim = 3,
}

/// <summary>An ST-Link's hardware generation and its JTAG/SWD firmware version.</summary>
internal readonly record struct StLinkVersion(int Hardware, int Jtag)
{
    /// <summary>The form ST's tools print, for example <c>V3J9</c>.</summary>
    public override string ToString() => $"V{Hardware}J{Jtag}";
}

/// <summary>One ST-Link hardware variant: its USB product id and bulk endpoints.</summary>
internal sealed record StLinkModel(string Name, ushort ProductId, byte OutEndpoint, byte InEndpoint);

/// <summary>
/// The ST-Link USB protocol as pure encoders and decoders. Every command is a 16-byte block on
/// the OUT endpoint, optionally followed by data; the reply is read from the IN endpoint.
/// </summary>
internal static class StLinkProtocol
{
    public const ushort VendorId = 0x0483;
    public const int CommandLength = 16;

    /// <summary>The largest 32-bit read one command carries (probe-rs, measured on a V2).</summary>
    public const int MaxRead32 = 6144;

    /// <summary>The largest 32-bit write one command carries: the largest 16-bit multiple of 4.</summary>
    public const int MaxWrite32 = 0xFFFC;

    /// <summary>The probe firmware floor (ADR-0094 D7): JTAG firmware 26 on V2 and V2-1.</summary>
    public const int MinJtagVersionV2 = 26;

    /// <summary>The probe firmware floor (ADR-0094 D7): JTAG firmware 3 on V3.</summary>
    public const int MinJtagVersionV3 = 3;

    /// <summary>Firmware from which a V2 accepts <c>JTAG_INIT_AP</c>. Every V3 accepts it.</summary>
    public const int MinJtagVersionInitAp = 28;

    // Command codes.
    private const byte GetVersionCode = 0xF1;
    private const byte JtagCommand = 0xF2;
    private const byte DfuCommand = 0xF3;
    private const byte SwimCommand = 0xF4;
    private const byte GetCurrentModeCode = 0xF5;
    private const byte GetTargetVoltageCode = 0xF7;
    private const byte GetVersionExtendedCode = 0xFB;
    private const byte DfuExitCode = 0x07;
    private const byte SwimExitCode = 0x01;
    private const byte ReadMem32Code = 0x07;
    private const byte WriteMem32Code = 0x08;
    private const byte JtagExitCode = 0x21;
    private const byte JtagEnter2Code = 0x30;
    private const byte WriteDebugRegCode = 0x35;
    private const byte ReadDebugRegCode = 0x36;
    private const byte GetLastRwStatus2Code = 0x3E;
    private const byte InitApCode = 0x4B;
    private const byte EnterSwdCode = 0xA3;

    // Reply lengths.
    public const int VersionReplyLength = 6;
    public const int VersionExtendedReplyLength = 12;
    public const int ModeReplyLength = 2;
    public const int VoltageReplyLength = 8;
    public const int StatusReplyLength = 2;
    public const int ReadDebugRegReplyLength = 8;
    public const int LastRwStatusReplyLength = 12;

    /// <summary>The hardware variants ST's debug driver lists, with probe-rs's endpoints.</summary>
    public static readonly FrozenDictionary<ushort, StLinkModel> Models = new StLinkModel[]
    {
        new("ST-Link/V2", 0x3748, 0x02, 0x81),
        new("ST-Link/V2-1", 0x374A, 0x01, 0x81),
        new("ST-Link/V2-1", 0x374B, 0x01, 0x81),
        new("ST-Link/V2-1", 0x3752, 0x01, 0x81),
        new("STLINK-V3", 0x374E, 0x01, 0x81),
        new("STLINK-V3", 0x374F, 0x01, 0x81),
        new("STLINK-V3", 0x3753, 0x01, 0x81),
        new("STLINK-V3", 0x3754, 0x01, 0x81),
        new("STLINK-V3", 0x3757, 0x01, 0x81),
    }.ToFrozenDictionary(m => m.ProductId);

    // ── encoders ────────────────────────────────────────────────────────

    public static byte[] GetVersion() => Command(GetVersionCode);

    public static byte[] GetVersionExtended() => Command(GetVersionExtendedCode);

    public static byte[] GetCurrentMode() => Command(GetCurrentModeCode);

    public static byte[] GetTargetVoltage() => Command(GetTargetVoltageCode);

    /// <summary>The command that leaves <paramref name="mode"/> for idle, or null when it is idle already.</summary>
    public static byte[]? ExitMode(StLinkMode mode) => mode switch
    {
        StLinkMode.Dfu => Command(DfuCommand, DfuExitCode),
        StLinkMode.Debug => Command(JtagCommand, JtagExitCode),
        StLinkMode.Swim => Command(SwimCommand, SwimExitCode),
        _ => null,
    };

    public static byte[] EnterSwd() => Command(JtagCommand, JtagEnter2Code, EnterSwdCode, 0);

    public static byte[] InitAp(byte ap) => Command(JtagCommand, InitApCode, ap);

    public static byte[] ReadMem32(uint address, int length, byte ap = 0) =>
        MemoryCommand(ReadMem32Code, address, length, MaxRead32, ap);

    public static byte[] WriteMem32(uint address, int length, byte ap = 0) =>
        MemoryCommand(WriteMem32Code, address, length, MaxWrite32, ap);

    public static byte[] GetLastRwStatus2() => Command(JtagCommand, GetLastRwStatus2Code);

    public static byte[] ReadDebugReg(uint address)
    {
        RequireWordAligned(address);
        var cmd = Command(JtagCommand, ReadDebugRegCode);
        BinaryPrimitives.WriteUInt32LittleEndian(cmd.AsSpan(2), address);
        return cmd;
    }

    public static byte[] WriteDebugReg(uint address, uint value)
    {
        RequireWordAligned(address);
        var cmd = Command(JtagCommand, WriteDebugRegCode);
        BinaryPrimitives.WriteUInt32LittleEndian(cmd.AsSpan(2), address);
        BinaryPrimitives.WriteUInt32LittleEndian(cmd.AsSpan(6), value);
        return cmd;
    }

    // ── decoders ────────────────────────────────────────────────────────

    /// <summary>
    /// Decodes <c>GET_VERSION</c>: a big-endian word holding the hardware version in bits 15:12
    /// and the JTAG firmware version in bits 11:6. A V3 reports its JTAG version only in the
    /// extended reply, so pass that too when the hardware is 3 or later.
    /// </summary>
    public static StLinkVersion DecodeVersion(ReadOnlySpan<byte> reply, ReadOnlySpan<byte> extendedReply = default)
    {
        RequireLength(reply, VersionReplyLength, "GET_VERSION");
        ushort word = BinaryPrimitives.ReadUInt16BigEndian(reply);
        int hardware = (word >> 12) & 0x0F;
        int jtag = (word >> 6) & 0x3F;
        if (hardware >= 3)
        {
            RequireLength(extendedReply, VersionExtendedReplyLength, "GET_VERSION_EXT");
            jtag = extendedReply[2];
        }
        return new StLinkVersion(hardware, jtag);
    }

    /// <summary>
    /// Null when <paramref name="version"/> meets the floor in ADR-0094 D7, otherwise why not.
    /// </summary>
    public static string? FirmwareFloorViolation(StLinkVersion version)
    {
        if (version.Jtag == 0)
            return $"ST-Link {version} has no JTAG/SWD firmware.";
        int floor = version.Hardware >= 3 ? MinJtagVersionV3 : MinJtagVersionV2;
        return version.Jtag < floor
            ? $"ST-Link firmware {version} is older than V{version.Hardware}J{floor}. Update it with ST's own tool first."
            : null;
    }

    /// <summary>Whether the probe accepts <c>JTAG_INIT_AP</c>.</summary>
    public static bool SupportsInitAp(StLinkVersion version) =>
        version.Hardware >= 3 || version.Jtag >= MinJtagVersionInitAp;

    public static StLinkMode DecodeMode(ReadOnlySpan<byte> reply)
    {
        RequireLength(reply, ModeReplyLength, "GET_CURRENT_MODE");
        return reply[0] <= (byte)StLinkMode.Swim
            ? (StLinkMode)reply[0]
            : throw new StLinkException($"ST-Link reported unknown mode 0x{reply[0]:X2}.");
    }

    /// <summary>Decodes <c>GET_TARGET_VOLTAGE</c> into volts: 2 × a1 × 1.2 / a0.</summary>
    public static double DecodeTargetVoltage(ReadOnlySpan<byte> reply)
    {
        RequireLength(reply, VoltageReplyLength, "GET_TARGET_VOLTAGE");
        uint a0 = BinaryPrimitives.ReadUInt32LittleEndian(reply);
        uint a1 = BinaryPrimitives.ReadUInt32LittleEndian(reply[4..]);
        return a0 == 0
            ? throw new StLinkException("ST-Link reported a zero voltage reference.")
            : 2.0 * a1 * 1.2 / a0;
    }

    /// <summary>The status in the first byte of a reply.</summary>
    public static StLinkStatus DecodeStatus(ReadOnlySpan<byte> reply) =>
        reply.IsEmpty ? throw new StLinkException("ST-Link sent an empty reply.") : (StLinkStatus)reply[0];

    /// <summary>The value of a <c>JTAG_READ_DEBUG_REG</c> reply, after its status.</summary>
    public static uint DecodeDebugRegValue(ReadOnlySpan<byte> reply)
    {
        RequireLength(reply, ReadDebugRegReplyLength, "JTAG_READ_DEBUG_REG");
        return BinaryPrimitives.ReadUInt32LittleEndian(reply[4..]);
    }

    /// <summary>Whether <paramref name="status"/> asks the host to retry the same command.</summary>
    public static bool IsWait(StLinkStatus status) =>
        status is StLinkStatus.SwdApWait or StLinkStatus.SwdDpWait or StLinkStatus.CommandWait;

    // ── helpers ─────────────────────────────────────────────────────────

    private static byte[] Command(params byte[] bytes)
    {
        var cmd = new byte[CommandLength];
        bytes.CopyTo(cmd, 0);
        return cmd;
    }

    private static byte[] MemoryCommand(byte code, uint address, int length, int max, byte ap)
    {
        RequireWordAligned(address);
        if (length <= 0 || length % 4 != 0 || length > max)
            throw new ArgumentOutOfRangeException(nameof(length), length,
                $"A 32-bit transfer is a positive multiple of 4 bytes, at most {max}.");

        var cmd = Command(JtagCommand, code);
        BinaryPrimitives.WriteUInt32LittleEndian(cmd.AsSpan(2), address);
        BinaryPrimitives.WriteUInt16LittleEndian(cmd.AsSpan(6), (ushort)length);
        cmd[8] = ap;
        return cmd;
    }

    private static void RequireWordAligned(uint address)
    {
        if (address % 4 != 0)
            throw new ArgumentException($"Address 0x{address:X8} is not word-aligned.", nameof(address));
    }

    private static void RequireLength(ReadOnlySpan<byte> reply, int length, string command)
    {
        if (reply.Length < length)
            throw new StLinkException($"{command} reply is {reply.Length} bytes; expected {length}.");
    }
}

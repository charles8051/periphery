// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Buffers.Binary;
using Periphery.DebugProbe;

namespace Periphery.DebugProbe.StLink.Tests;

/// <summary>
/// A fake ST-Link that answers the commands <see cref="StLinkSession"/> sends, backed by a target
/// behind the memory seam. It decodes each 16-byte command block the way the probe does, so the
/// session's framing is checked byte for byte.
/// </summary>
internal sealed class FakeStLinkProbe : IStLinkTransport
{
    private readonly ITargetMemory _target;

    public FakeStLinkProbe(ITargetMemory target) => _target = target;

    public byte Mode { get; set; } = 1; // mass storage, as a freshly plugged probe reports
    public ushort VersionWord { get; set; } = (3 << 12) | (0 << 6); // V3; its JTAG version is in the extended reply
    public byte ExtendedJtagVersion { get; set; } = 9;
    public (uint A0, uint A1) Voltage { get; set; } = (1000, 1375); // 3.30 V
    public Queue<byte> MemoryStatuses { get; } = new();
    public Queue<byte> CommandStatuses { get; } = new();
    public List<byte[]> Commands { get; } = [];

    private byte _lastRwStatus = 0x80;

    public async Task<byte[]> ExchangeAsync(byte[] command, ReadOnlyMemory<byte> dataOut, int replyLength, CancellationToken ct)
    {
        Assert.Equal(StLinkProtocol.CommandLength, command.Length);
        Commands.Add(command);

        byte[] reply = command[0] switch
        {
            0xF5 => [Mode, 0],
            0xF3 or 0xF4 => [],
            0xF1 => Version(),
            0xFB => [3, 0, ExtendedJtagVersion, 0, 0, 0, 0, 0, 0x83, 0x04, 0x4E, 0x37],
            0xF7 => Words(Voltage.A0, Voltage.A1),
            0xF2 => await JtagAsync(command, dataOut, ct),
            _ => throw new InvalidOperationException($"Unexpected command 0x{command[0]:X2}."),
        };
        Assert.Equal(replyLength, reply.Length);
        return reply;
    }

    private byte[] Version()
    {
        var reply = new byte[6];
        BinaryPrimitives.WriteUInt16BigEndian(reply, VersionWord);
        return reply;
    }

    private async Task<byte[]> JtagAsync(byte[] command, ReadOnlyMemory<byte> dataOut, CancellationToken ct)
    {
        uint address = BinaryPrimitives.ReadUInt32LittleEndian(command.AsSpan(2));
        switch (command[1])
        {
            case 0x21: // JTAG_EXIT
                return [];
            case 0x30: // JTAG_ENTER2
                Assert.Equal(0xA3, command[2]);
                return Status();
            case 0x4B: // JTAG_INIT_AP
                return Status();
            case 0x36: // READ_DEBUG_REG
            {
                var status = Status();
                if (status[0] != 0x80)
                    return [status[0], 0, 0, 0, 0, 0, 0, 0];
                uint value = await _target.ReadWordAsync(address, ct);
                var reply = new byte[8];
                reply[0] = 0x80;
                BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(4), value);
                return reply;
            }
            case 0x35: // WRITE_DEBUG_REG
            {
                var status = Status();
                if (status[0] == 0x80)
                    await _target.WriteWordAsync(address, BinaryPrimitives.ReadUInt32LittleEndian(command.AsSpan(6)), ct);
                return status;
            }
            case 0x07: // READMEM_32BIT
            {
                int length = BinaryPrimitives.ReadUInt16LittleEndian(command.AsSpan(6));
                var data = new byte[length];
                _lastRwStatus = MemoryStatuses.TryDequeue(out var s) ? s : (byte)0x80;
                if (_lastRwStatus == 0x80)
                    _lastRwStatus = await FaultIfUnmappedAsync(() => _target.ReadAsync(address, data, ct));
                return data;
            }
            case 0x08: // WRITEMEM_32BIT
            {
                int length = BinaryPrimitives.ReadUInt16LittleEndian(command.AsSpan(6));
                Assert.Equal(length, dataOut.Length);
                _lastRwStatus = MemoryStatuses.TryDequeue(out var s) ? s : (byte)0x80;
                if (_lastRwStatus == 0x80)
                    _lastRwStatus = await FaultIfUnmappedAsync(() => _target.WriteAsync(address, dataOut, ct));
                return [];
            }
            case 0x3E: // GETLASTRWSTATUS2
            {
                var reply = new byte[12];
                reply[0] = _lastRwStatus;
                return reply;
            }
            default:
                throw new InvalidOperationException($"Unexpected JTAG command 0x{command[1]:X2}.");
        }
    }

    /// <summary>A real probe answers an access to unmapped memory with an AP fault, not an error on the wire.</summary>
    private static async Task<byte> FaultIfUnmappedAsync(Func<Task> access)
    {
        try
        {
            await access();
            return 0x80;
        }
        catch (UnmappedMemoryException)
        {
            return (byte)StLinkStatus.SwdApFault;
        }
    }

    private byte[] Status() => [CommandStatuses.TryDequeue(out var s) ? s : (byte)0x80, 0];

    private static byte[] Words(uint a, uint b)
    {
        var reply = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(reply, a);
        BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(4), b);
        return reply;
    }
}

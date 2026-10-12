// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Buffers.Binary;
using Microsoft.Extensions.Time.Testing;
using Periphery.DebugProbe;
using Periphery.DebugProbe.CortexM;
using Periphery.DebugProbe.Flash;
using Periphery.DebugProbe.Targets;

namespace Periphery.DebugProbe.StLink.Tests;

/// <summary>
/// A pure model of an STM32G431 behind the memory seam: its identity registers, the Cortex-M
/// debug registers, RAM, and flash that only the flash algorithm can change. Resuming the core
/// at an algorithm entry point runs that function's effect and halts at LR, so the shell is
/// exercised end to end without hardware.
/// </summary>
internal sealed class SimulatedStm32G431 : ITargetMemory
{
    public const uint FlashStart = 0x08000000;
    public const uint FlashSize = 128 * 1024;
    public const uint RamStart = 0x20000000;
    public const uint RamSize = 32 * 1024;

    private const uint DhcsrSHalt = 1u << 17;
    private const uint DhcsrSRegRdy = 1u << 16;
    private const uint DhcsrSResetSt = 1u << 25;

    private readonly FlashAlgorithm _algorithm;
    private readonly uint[] _registers = new uint[17];
    private uint _dcrdr;
    private uint _demcr;
    private uint _apb1Freeze;
    private bool _debugEnabled;
    private bool _resetSticky;

    public SimulatedStm32G431(FlashAlgorithm algorithm, FakeTimeProvider time, byte[]? flash = null)
    {
        _algorithm = algorithm;
        Time = time;
        Flash = flash ?? Enumerable.Repeat((byte)0xFF, (int)FlashSize).ToArray();
    }

    public FakeTimeProvider Time { get; }

    public byte[] Flash { get; }

    public byte[] Ram { get; } = new byte[RamSize];

    public uint Idcode { get; init; } = 0x20036468;

    public uint FlashSizeData { get; init; } = 0xFFFF0080;

    public uint Optr { get; init; } = 0xFBEFF8AA;

    public bool Halted { get; private set; }

    /// <summary>When true, writes to RAM are dropped, as at an address with no RAM behind it.</summary>
    public bool RamDropsWrites { get; init; }

    /// <summary>When false, the core ignores reset vector catch and runs after a reset.</summary>
    public bool HonoursResetCatch { get; init; } = true;

    /// <summary>How many DHCSR polls an algorithm call stays busy for.</summary>
    public int BusyPolls { get; init; } = 2;

    /// <summary>When set, the next call of this function returns this non-zero result.</summary>
    public (string Function, uint Result)? FailNext { get; set; }

    public int FlashWritesThroughSeam { get; private set; }

    public uint WatchdogFreeze => _apb1Freeze;

    public List<string> Calls { get; } = [];

    public bool Reset { get; private set; }

    public bool RunningApplication { get; private set; }

    private int _busy;
    private (Action Effect, uint Lr)? _pending;

    public Task<uint> ReadWordAsync(uint address, CancellationToken ct)
    {
        Time.Advance(TimeSpan.FromMilliseconds(1));
        return Task.FromResult(address switch
        {
            Stm32Registers.DbgmcuIdcode => Idcode,
            Stm32Registers.FlashSizeData => FlashSizeData,
            Stm32Registers.FlashOptr => Optr,
            Stm32Registers.DbgmcuApb1Freeze => _apb1Freeze,
            CortexMRegisters.Dhcsr => ReadDhcsr(),
            CortexMRegisters.Dcrdr => _dcrdr,
            CortexMRegisters.Demcr => _demcr,
            _ => ReadPlain(address),
        });
    }

    public Task WriteWordAsync(uint address, uint value, CancellationToken ct)
    {
        switch (address)
        {
            case CortexMRegisters.Dhcsr:
                WriteDhcsr(value);
                break;
            case CortexMRegisters.Dcrdr:
                _dcrdr = value;
                break;
            case CortexMRegisters.Dcrsr:
                if (!Halted)
                    throw new InvalidOperationException("Core registers are only reachable while halted.");
                uint sel = value & 0x7F;
                if ((value & (1u << 16)) != 0)
                    _registers[sel] = _dcrdr;
                else
                    _dcrdr = _registers[sel];
                break;
            case CortexMRegisters.Demcr:
                _demcr = value;
                break;
            case CortexMRegisters.Aircr:
                if (value != CortexMRegisters.AircrSystemReset)
                    throw new InvalidOperationException($"Unexpected AIRCR write 0x{value:X8}.");
                SystemReset();
                break;
            case Stm32Registers.DbgmcuApb1Freeze:
                _apb1Freeze = value;
                break;
            default:
                Span<byte> bytes = stackalloc byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
                WritePlain(address, bytes);
                break;
        }
        return Task.CompletedTask;
    }

    public Task ReadAsync(uint address, Memory<byte> destination, CancellationToken ct)
    {
        Region(address, destination.Length, out var region, out int offset);
        region.AsSpan(offset, destination.Length).CopyTo(destination.Span);
        return Task.CompletedTask;
    }

    public Task WriteAsync(uint address, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        WritePlain(address, data.Span);
        return Task.CompletedTask;
    }

    // ── core ────────────────────────────────────────────────────────────

    private uint ReadDhcsr()
    {
        if (_pending is { } pending && --_busy <= 0)
        {
            pending.Effect();
            _registers[(int)CoreRegister.DebugReturnAddress] = pending.Lr & ~1u;
            _pending = null;
            Halted = true;
        }

        uint value = DhcsrSRegRdy | (Halted ? DhcsrSHalt : 0) | (_resetSticky ? DhcsrSResetSt : 0);
        _resetSticky = false;
        return value;
    }

    private void WriteDhcsr(uint value)
    {
        if (value >> 16 != 0xA05F)
            throw new InvalidOperationException("DHCSR written without DBGKEY.");
        _debugEnabled = (value & 1) != 0;
        bool halt = (value & 2) != 0;
        if (halt)
        {
            Halted = true;
            RunningApplication = false;
        }
        else if (Halted && _debugEnabled)
        {
            Halted = false;
            StartCall();
        }
    }

    private void SystemReset()
    {
        Reset = true;
        _resetSticky = true;
        _pending = null;
        Array.Clear(_registers);
        _registers[(int)CoreRegister.Xpsr] = CortexMRegisters.XpsrThumb;
        bool catchReset = HonoursResetCatch && _debugEnabled && (_demcr & 1) != 0;
        Halted = catchReset;
        RunningApplication = !catchReset;
    }

    private void StartCall()
    {
        uint code = RamStart + (uint)RamLayout.BreakpointHeader.Length;
        uint pc = _registers[(int)CoreRegister.DebugReturnAddress];
        uint lr = _registers[(int)CoreRegister.Lr];
        if (lr != (RamStart | 1))
            throw new InvalidOperationException($"LR 0x{lr:X8} does not return to the breakpoint header.");
        if ((_registers[(int)CoreRegister.Xpsr] & CortexMRegisters.XpsrThumb) == 0)
            throw new InvalidOperationException("xPSR has no Thumb bit; the core would fault.");
        if (!Ram.AsSpan(4, _algorithm.Code.Length).SequenceEqual(_algorithm.Code.Span))
            throw new InvalidOperationException("Resumed into an algorithm that was never loaded.");

        uint r0 = _registers[0], r1 = _registers[1], r2 = _registers[2];
        string function = (pc - code) switch
        {
            var o when o == _algorithm.InitOffset => "Init",
            var o when o == _algorithm.UnInitOffset => "UnInit",
            var o when o == _algorithm.EraseSectorOffset => "EraseSector",
            var o when o == _algorithm.ProgramPageOffset => "ProgramPage",
            _ => throw new InvalidOperationException($"Resumed at 0x{pc:X8}, which is no entry point."),
        };

        Action effect = function switch
        {
            "EraseSector" => () => Result(EraseSector(r0)),
            "ProgramPage" => () => Result(ProgramPage(r0, r1, r2)),
            _ => () => Result(0),
        };
        if (FailNext is { } fail && fail.Function == function)
        {
            FailNext = null;
            effect = () => Result(fail.Result);
        }

        Calls.Add(function == "Init" || function == "UnInit" ? $"{function}({(function == "Init" ? r2 : r0)})" : $"{function}(0x{r0:X8})");
        _pending = (effect, lr);
        _busy = BusyPolls;
    }

    private void Result(uint value) => _registers[0] = value;

    private uint EraseSector(uint address)
    {
        var (start, size) = _algorithm.SectorContaining(address);
        if (start != address)
            return 1;
        Flash.AsSpan((int)(start - FlashStart), (int)size).Fill(0xFF);
        return 0;
    }

    private uint ProgramPage(uint address, uint size, uint buffer)
    {
        var source = Ram.AsSpan((int)(buffer - RamStart), (int)size);
        var target = Flash.AsSpan((int)(address - FlashStart), (int)size);
        if (target.IndexOfAnyExcept((byte)0xFF) >= 0)
            return 1; // programming over bytes that are not erased
        source.CopyTo(target);
        return 0;
    }

    // ── plain memory ────────────────────────────────────────────────────

    private uint ReadPlain(uint address)
    {
        Region(address, 4, out var region, out int offset);
        return BinaryPrimitives.ReadUInt32LittleEndian(region.AsSpan(offset));
    }

    private void WritePlain(uint address, ReadOnlySpan<byte> data)
    {
        if (address >= FlashStart && address < FlashStart + FlashSize)
        {
            FlashWritesThroughSeam++;
            throw new InvalidOperationException("Flash is written through the seam; only the algorithm may write it.");
        }
        Region(address, data.Length, out var region, out int offset);
        if (RamDropsWrites && region == Ram)
            return;
        data.CopyTo(region.AsSpan(offset));
    }

    private void Region(uint address, int length, out byte[] region, out int offset)
    {
        if (address >= FlashStart && address + (uint)length <= FlashStart + FlashSize)
        {
            region = Flash;
            offset = (int)(address - FlashStart);
        }
        else if (address >= RamStart && address + (uint)length <= RamStart + RamSize)
        {
            region = Ram;
            offset = (int)(address - RamStart);
        }
        else
        {
            throw new UnmappedMemoryException(address, length);
        }
    }
}

/// <summary>An access to an address with no memory behind it; a real probe reports an AP fault.</summary>
internal sealed class UnmappedMemoryException(uint address, int length)
    : Exception($"No memory at 0x{address:X8}+{length}.");

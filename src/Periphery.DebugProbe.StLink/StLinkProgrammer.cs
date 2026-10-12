// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Immutable;
using Periphery.Bootloader;
using Periphery.DebugProbe.CortexM;
using Periphery.DebugProbe.Flash;
using Periphery.DebugProbe.Targets;
using Periphery.Firmware;
using Periphery.Usb;

namespace Periphery.DebugProbe.StLink;

/// <summary>
/// Programs an STM32's flash over SWD through an ST-Link (ADR-0094). Identifies the target from
/// its debug registers, refuses anything off the allowlist or read-protected, then runs the
/// vendor's CMSIS-Pack flash algorithm on the halted core.
/// </summary>
/// <remarks>
/// Every refusal happens before the core is halted. A failure after that leaves the core halted:
/// flash may be partly erased, and running it would start whatever is left.
/// </remarks>
public sealed class StLinkProgrammer : IFirmwareProgrammer
{
    private static readonly ImmutableArray<FirmwareFormat> s_acceptedFormats =
        [FirmwareFormat.IntelHex, FirmwareFormat.RawBinary, FirmwareFormat.Elf];

    private readonly UsbDevice? _usb;            // null in unit tests
    private readonly StLinkSession? _session;    // null in unit tests
    private readonly ITargetMemory _memory;
    private readonly CortexMDebug _core;
    private readonly string _probeFirmware;

    private StLinkProgrammer(
        DeviceInfo device, UsbDevice? usb, StLinkSession? session, ITargetMemory memory, TimeProvider time,
        string probeFirmware, double targetVoltage)
    {
        Device = device;
        _usb = usb;
        _session = session;
        _memory = memory;
        _core = new CortexMDebug(memory, time);
        _probeFirmware = probeFirmware;
        TargetVoltage = targetVoltage;
    }

    /// <inheritdoc />
    public DeviceInfo Device { get; }

    /// <inheritdoc />
    public ImmutableArray<FirmwareFormat> AcceptedFormats => s_acceptedFormats;

    /// <summary>The target supply the probe measured when it was opened, in volts.</summary>
    public double TargetVoltage { get; }

    /// <summary>Opens the ST-Link's debug function and enters SWD. The target is not halted.</summary>
    public static async Task<StLinkProgrammer> OpenAsync(DeviceInfo device, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.ProductId is not { } pid || !StLinkProtocol.Models.TryGetValue((ushort)pid.Value, out var model))
            throw new ArgumentException($"{device.Id} is not an ST-Link.", nameof(device));

        var usb = await UsbDevice.OpenAsync(device, ct, transferTimeout: TimeSpan.FromSeconds(2), logger: null)
            .ConfigureAwait(false);
        try
        {
            var session = await StLinkSession.OpenAsync(new UsbStLinkTransport(usb, model), ct).ConfigureAwait(false);
            return new StLinkProgrammer(
                device, usb, session, session, TimeProvider.System, session.Version.ToString(), session.TargetVoltage);
        }
        catch
        {
            await usb.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Test factory: drives the programmer against a fake target, no hardware.</summary>
    internal static StLinkProgrammer CreateForTest(DeviceInfo device, ITargetMemory memory, TimeProvider time) =>
        new(device, usb: null, session: null, memory, time, "V3J9", 3.3);

    /// <inheritdoc />
    public async Task<DeviceIdentity> IdentifyAsync(CancellationToken ct = default)
    {
        var target = await ReadTargetAsync(ct).ConfigureAwait(false);
        var regions = target.Known is { Algorithm: var algorithm }
            ? ImmutableArray.Create(new MemoryRegion(
                "Main flash", algorithm.FlashStart, Math.Min(algorithm.FlashSize, target.FlashSize),
                algorithm.Sectors[0].Size, MemoryAccess.Readable | MemoryAccess.Writable | MemoryAccess.Erasable))
            : ImmutableArray<MemoryRegion>.Empty;

        return new DeviceIdentity(
            Family: target.Known?.Family ?? "STM32",
            Chip: target.Known?.Chip ?? $"unknown (DEV_ID 0x{target.DeviceId:X3})",
            BootloaderVersion: _probeFirmware,
            TransferSize: (int)(target.Known?.Algorithm.PageSize ?? 0),
            Regions: regions,
            SupportedCommands: ImmutableArray<string>.Empty);
    }

    /// <inheritdoc />
    public async Task<FlashResult> FlashAsync(
        FirmwarePayload payload, FlashOptions options, IProgress<FlashProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(options);

        if (!s_acceptedFormats.Contains(payload.Format) || payload.MemoryImage is not { } image)
            return FlashResult.Fail(
                $"The ST-Link flasher cannot flash {payload.Format}; it accepts {string.Join(", ", s_acceptedFormats)}.");

        try
        {
            // Refusals, before the core is touched (ADR-0094 D5).
            progress?.Report(new FlashProgress(FlashPhase.Identifying, 0, image.TotalBytes));
            var target = await ReadTargetAsync(ct).ConfigureAwait(false);
            if (target.Known is not { Algorithm: var algorithm })
                return FlashResult.Fail(
                    $"Device ID 0x{target.DeviceId:X3} is not on the ST-Link flasher's allowlist.");
            if (target.Protection != ReadProtection.Level0)
                return FlashResult.Fail(
                    $"{target.Known.Chip} has read protection {target.Protection}. Refusing: lowering it mass-erases the part.");
            var sectors = FlashPlan.SectorsToRead(image, algorithm, target.FlashSize);
            var layout = RamLayout.For(algorithm);

            // Halt from reset, so the application is not running while its flash changes.
            progress?.Report(new FlashProgress(FlashPhase.Connecting, 0, image.TotalBytes, "Reset and halt"));
            await _core.ResetAndHaltAsync(ct).ConfigureAwait(false);
            uint freeze = await _memory.ReadWordAsync(Stm32Registers.DbgmcuApb1Freeze, ct).ConfigureAwait(false);
            await _memory.WriteWordAsync(Stm32Registers.DbgmcuApb1Freeze, freeze | Stm32Registers.WatchdogStopBits, ct)
                .ConfigureAwait(false);

            var current = new List<SectorContents>(sectors.Count);
            foreach (var (address, size) in sectors)
                current.Add(new SectorContents(address, await ReadAsync(address, (int)size, ct).ConfigureAwait(false)));
            var merged = FlashPlan.Merge(current, image);
            var steps = FlashPlan.Steps(merged, algorithm, layout);

            if (steps.Count > 0)
            {
                var runner = new FlashAlgorithmRunner(_memory, _core);
                await runner.LoadAsync(algorithm, layout, ct).ConfigureAwait(false);
                await runner.ExecuteAsync(steps, algorithm, layout, progress, ct).ConfigureAwait(false);
            }

            if (options.Verify)
            {
                progress?.Report(new FlashProgress(FlashPhase.Verifying, 0, image.TotalBytes));
                foreach (var sector in merged)
                    await VerifyAsync(sector, ct).ConfigureAwait(false);
            }

            if (options.LeaveAfterFlash)
            {
                progress?.Report(new FlashProgress(FlashPhase.Leaving, image.TotalBytes, image.TotalBytes));
                await _core.ResetAndRunAsync(ct).ConfigureAwait(false);
            }

            progress?.Report(new FlashProgress(FlashPhase.Done, image.TotalBytes, image.TotalBytes));
            return FlashResult.Ok(image.TotalBytes, verified: options.Verify);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is FlashPlanException or FlashAlgorithmException or CortexMException or StLinkException
                                       or UsbException or VerifyException)
        {
            return FlashResult.Fail(ex);
        }
    }

    /// <summary>Resets the target and lets it run, with halting debug turned off.</summary>
    public Task LeaveAsync(CancellationToken ct = default) => _core.ResetAndRunAsync(ct);

    /// <summary>Reads target memory, for example to keep a copy of flash before writing it.</summary>
    public async Task<byte[]> ReadAsync(uint address, int length, CancellationToken ct = default)
    {
        if (address % 4 != 0 || length % 4 != 0)
            throw new ArgumentException("Reads are word-aligned and a whole number of words.");
        var data = new byte[length];
        await _memory.ReadAsync(address, data, ct).ConfigureAwait(false);
        return data;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_session is not null)
        {
            try
            {
                await _session.CloseAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is StLinkException or UsbException)
            {
                // The probe may already be gone; there is nothing to return to idle.
            }
        }
        if (_usb is not null)
            await _usb.DisposeAsync().ConfigureAwait(false);
    }

    private sealed record TargetReading(ushort DeviceId, uint FlashSize, ReadProtection Protection, Stm32Target? Known);

    private async Task<TargetReading> ReadTargetAsync(CancellationToken ct)
    {
        uint idcode = await _memory.ReadWordAsync(Stm32Registers.DbgmcuIdcode, ct).ConfigureAwait(false);
        uint flashSize = await _memory.ReadWordAsync(Stm32Registers.FlashSizeData, ct).ConfigureAwait(false);
        uint optr = await _memory.ReadWordAsync(Stm32Registers.FlashOptr, ct).ConfigureAwait(false);
        ushort deviceId = Stm32Registers.DeviceId(idcode);
        return new TargetReading(
            deviceId,
            Stm32Registers.FlashSizeBytes(flashSize),
            Stm32Registers.ReadProtectionOf(optr),
            Stm32Catalog.Targets.GetValueOrDefault(deviceId));
    }

    private async Task VerifyAsync(MergedSector sector, CancellationToken ct)
    {
        var actual = await ReadAsync(sector.Address, sector.Data.Length, ct).ConfigureAwait(false);
        int mismatch = actual.AsSpan().CommonPrefixLength(sector.Data.Span);
        if (mismatch < actual.Length)
            throw new VerifyException(
                $"Verify failed at 0x{sector.Address + (uint)mismatch:X8}: flash has 0x{actual[mismatch]:X2}, " +
                $"expected 0x{sector.Data.Span[mismatch]:X2}.");
    }

    private sealed class VerifyException(string message) : Exception(message);
}

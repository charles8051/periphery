// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Periphery.Firmware;
using Periphery.Usb;

namespace Periphery.Bootloader.Stm32.Usb;

/// <summary>
/// The imperative shell (ADR-0052): drives the AN3156 USB DFU protocol over the transport
/// seam to identify and flash an STM32 already in DFU mode. Owns the USB handle, the
/// GETSTATUS poll loop, and all timing. Implements the platform contract
/// <see cref="IFirmwareProgrammer"/>.
/// </summary>
/// <remarks>
/// Scope: flash a device already in DFU mode (VID 0x0483 / PID 0xDF11) — mass-erase + app-image
/// write + read-back verify (DFU_UPLOAD) + leave. Per-page erase and the guarded Read-Unprotect /
/// option-byte ops are still phase 2.
/// </remarks>
public sealed class Stm32DfuProgrammer : IFirmwareProgrammer
{
    private const int FallbackTransferSize = 1024;

    private readonly UsbDevice? _usb;            // null in unit tests (transport injected)
    private readonly IStm32DfuTransport _transport;
    private readonly int _transferSize;
    private readonly string? _bootloaderVersion;

    private Stm32DfuProgrammer(
        DeviceInfo device, UsbDevice? usb, IStm32DfuTransport transport, int transferSize, string? bootloaderVersion)
    {
        Device = device;
        _usb = usb;
        _transport = transport;
        _transferSize = transferSize;
        _bootloaderVersion = bootloaderVersion;
    }

    /// <inheritdoc />
    public DeviceInfo Device { get; }

    private static readonly ImmutableArray<FirmwareFormat> s_acceptedFormats =
        ImmutableArray.Create(FirmwareFormat.IntelHex, FirmwareFormat.RawBinary, FirmwareFormat.Elf);

    /// <inheritdoc />
    public ImmutableArray<FirmwareFormat> AcceptedFormats => s_acceptedFormats;

    /// <summary>Test factory: drives the shell against a fake transport, no hardware.</summary>
    internal static Stm32DfuProgrammer CreateForTest(
        DeviceInfo device, IStm32DfuTransport transport, int transferSize, string? bootloaderVersion = null)
        => new(device, usb: null, transport, transferSize, bootloaderVersion);

    /// <summary>
    /// Opens the STM32 DFU device, reads its bootloader version and DFU transfer size, and
    /// brings it to dfuIDLE. The device must already be in DFU mode.
    /// </summary>
    public static async Task<Stm32DfuProgrammer> OpenAsync(DeviceInfo device, byte interfaceNumber = 0, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);

        var usb = await UsbDevice.OpenAsync(device, ct).ConfigureAwait(false);
        try
        {
            string version = FormatBootloaderVersion(usb.Descriptor.DeviceVersion);
            int transferSize = await ProbeTransferSizeAsync(usb, ct).ConfigureAwait(false);
            var transport = new UsbStm32DfuTransport(usb, interfaceNumber);
            var programmer = new Stm32DfuProgrammer(device, usb, transport, transferSize, version);
            await programmer.EnsureIdleAsync(ct).ConfigureAwait(false);
            return programmer;
        }
        catch
        {
            await usb.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<DeviceIdentity> IdentifyAsync(CancellationToken ct = default)
    {
        var commands = await GetSupportedCommandsAsync(ct).ConfigureAwait(false);
        return new DeviceIdentity(
            Family: "STM32",
            Chip: null,                              // chip / memory-map resolution is phase 2
            BootloaderVersion: _bootloaderVersion,
            TransferSize: _transferSize,
            Regions: ImmutableArray<MemoryRegion>.Empty,
            SupportedCommands: commands);
    }

    /// <inheritdoc />
    public async Task<FlashResult> FlashAsync(
        FirmwarePayload payload, FlashOptions options, IProgress<FlashProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(options);

        // Safety gate: STM32 DFU writes addressed bytes, so it flashes only Kind-1 memory images.
        if (!s_acceptedFormats.Contains(payload.Format) || payload.MemoryImage is not { } image)
            return FlashResult.Fail(
                $"STM32 DFU cannot flash {payload.Format}; it accepts {string.Join(", ", s_acceptedFormats)}.");

        try
        {
            await EnsureIdleAsync(ct).ConfigureAwait(false);

            var steps = Stm32DfuPlan.Plan(image, _transferSize, options);
            long total = image.TotalBytes;
            long done = 0;

            foreach (var step in steps)
            {
                ct.ThrowIfCancellationRequested();
                switch (step)
                {
                    case DfuStep.MassErase:
                        progress?.Report(new FlashProgress(FlashPhase.Erasing, 0, total, "Mass erase"));
                        await CommandAndWaitAsync(Stm32DfuCommand.MassErase.Instance, ct).ConfigureAwait(false);
                        break;

                    case DfuStep.SetAddress setAddress:
                        await CommandAndWaitAsync(new Stm32DfuCommand.SetAddress(setAddress.Address), ct).ConfigureAwait(false);
                        break;

                    case DfuStep.WriteBlock write:
                        await DownloadAndWaitAsync(write.BlockNum, write.Data, ct).ConfigureAwait(false);
                        done += write.Data.Length;
                        progress?.Report(new FlashProgress(FlashPhase.Writing, done, total));
                        break;

                    case DfuStep.Verify verify:
                        progress?.Report(new FlashProgress(FlashPhase.Verifying, done, total, "Verify"));
                        await VerifySegmentAsync(verify.Address, verify.Expected, ct).ConfigureAwait(false);
                        break;

                    case DfuStep.Leave:
                        progress?.Report(new FlashProgress(FlashPhase.Leaving, total, total));
                        await LeaveInternalAsync(ct).ConfigureAwait(false);
                        break;
                }
            }

            progress?.Report(new FlashProgress(FlashPhase.Done, total, total));
            return FlashResult.Ok(total, verified: options.Verify);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Stm32DfuException ex)
        {
            return FlashResult.Fail(ex);
        }
    }

    /// <inheritdoc />
    public Task LeaveAsync(CancellationToken ct = default) => LeaveInternalAsync(ct);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_usb is not null)
            await _usb.DisposeAsync().ConfigureAwait(false);
    }

    // ── shell helpers (own the GETSTATUS poll loop + timing, ADR-0052 DEC-004) ──

    private async Task EnsureIdleAsync(CancellationToken ct)
    {
        // AN3156: before a request the device must be in a clean idle state; clear any error
        // (CLRSTATUS) or abort a stray idle until it returns to dfuIDLE.
        for (int attempt = 0; attempt < 4; attempt++)
        {
            var status = await _transport.GetStatusAsync(ct).ConfigureAwait(false);
            if (status.State == DfuState.DfuIdle)
                return;
            if (status.State == DfuState.DfuError)
                await _transport.ClearStatusAsync(ct).ConfigureAwait(false);
            else
                await _transport.AbortAsync(ct).ConfigureAwait(false);
        }

        var final = await _transport.GetStatusAsync(ct).ConfigureAwait(false);
        if (final.State != DfuState.DfuIdle)
            throw new Stm32DfuException(
                $"device did not return to dfuIDLE (state {final.State}, status {final.Status}).", final);
    }

    private Task CommandAndWaitAsync(Stm32DfuCommand command, CancellationToken ct)
        => DownloadAndWaitAsync(blockNum: 0, command.Encode(), ct);

    // A DNLOAD does nothing until GETSTATUS triggers it; the device reports dfuDNBUSY +
    // bwPollTimeout. Wait that long, then a second GETSTATUS confirms completion or reports
    // the error (AN3156 §5.1).
    private async Task DownloadAndWaitAsync(ushort blockNum, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        await _transport.DownloadAsync(blockNum, data, ct).ConfigureAwait(false);

        var busy = await _transport.GetStatusAsync(ct).ConfigureAwait(false);
        ThrowIfError(busy);

        await Task.Delay(busy.PollTimeout, ct).ConfigureAwait(false);

        var done = await _transport.GetStatusAsync(ct).ConfigureAwait(false);
        ThrowIfError(done);
    }

    // Read a segment back via DFU_UPLOAD and compare to what we wrote (AN3156 §5.2). Mirrors
    // dfu-util's dfuse upload: set the read pointer (the same Set-Address command as a write),
    // ABORT to dfuIDLE (the pointer is retained), then UPLOAD blocks from wBlockNum 2 — block N
    // reads at pointer + (N - 2) * wTransferSize, exactly mirroring the write addressing. A
    // mismatch or short read throws, which aborts the flash before Leave (so a bad image is not
    // started, and the device stays in DFU for a re-flash).
    private async Task VerifySegmentAsync(uint address, ReadOnlyMemory<byte> expected, CancellationToken ct)
    {
        await CommandAndWaitAsync(new Stm32DfuCommand.SetAddress(address), ct).ConfigureAwait(false);
        await _transport.AbortAsync(ct).ConfigureAwait(false);

        var buffer = new byte[_transferSize];
        int verified = 0;
        ushort block = 2;
        while (verified < expected.Length)
        {
            ct.ThrowIfCancellationRequested();
            int want = Math.Min(_transferSize, expected.Length - verified);
            int read = await _transport.UploadAsync(block, buffer.AsMemory(0, want), ct).ConfigureAwait(false);
            if (read < want)
                throw new Stm32DfuException(
                    $"verify: short read-back at 0x{address + (uint)verified:X8} (got {read} of {want} bytes).");

            VerifyChunk(buffer, want, expected, verified, address); // span compare in a sync helper (an async method can't hold a ref-struct local across an await)

            verified += want;
            block++;
        }

        await _transport.AbortAsync(ct).ConfigureAwait(false); // back to dfuIDLE for the next segment / leave
    }

    // Compare one read-back chunk to the image; throw on the first mismatch with its absolute
    // address. Synchronous so the ref-struct spans never live in the async caller.
    private static void VerifyChunk(byte[] readBack, int length, ReadOnlyMemory<byte> expected, int offset, uint baseAddress)
    {
        var got = readBack.AsSpan(0, length);
        var exp = expected.Span.Slice(offset, length);
        if (got.SequenceEqual(exp))
            return;

        int off = 0;
        while (off < length && got[off] == exp[off]) off++;
        uint at = baseAddress + (uint)offset + (uint)off;
        throw new Stm32DfuException(
            $"verify FAILED at 0x{at:X8}: flash has 0x{got[off]:X2} but the image has 0x{exp[off]:X2} " +
            "(read-back mismatch — the write did not land correctly).");
    }

    private async Task LeaveInternalAsync(CancellationToken ct)
    {
        // Leave = a zero-length DNLOAD, then GETSTATUS triggers manifestation, after which the
        // device resets and detaches (AN3156 §5.5). Once it drops, EITHER transfer can fail with
        // a transport error (e.g. a raw UsbException, "WinUSB control transfer failed") — that is
        // the expected, successful outcome, not a fault, so the whole sequence is wrapped. Only an
        // explicit dfuERROR (device still present and rejecting the leave) is a real failure.
        try
        {
            await _transport.DownloadAsync(0, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
            var status = await _transport.GetStatusAsync(ct).ConfigureAwait(false);
            if (status.State == DfuState.DfuError)
                throw new Stm32DfuException($"leave/manifest rejected: status {status.Status}.", status);
        }
        catch (Stm32DfuException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // Device dropped during leave/manifestation — expected; the flash already completed.
        }
    }

    private async Task<ImmutableArray<string>> GetSupportedCommandsAsync(CancellationToken ct)
    {
        // DFU_UPLOAD with wBlockNum = 0 returns the supported command codes (AN3156 §4.2).
        try
        {
            var buffer = new byte[64];
            int read = await _transport.UploadAsync(0, buffer, ct).ConfigureAwait(false);
            var names = ImmutableArray.CreateBuilder<string>();
            for (int i = 0; i < read; i++)
                names.Add(DescribeCommand(buffer[i]));
            return names.ToImmutable();
        }
        catch (Stm32DfuException)
        {
            return ImmutableArray<string>.Empty; // Get is informational, not load-bearing
        }
    }

    private static void ThrowIfError(DfuStatus status)
    {
        if (status.Status != DfuStatusCode.Ok || status.State == DfuState.DfuError)
            throw new Stm32DfuException($"operation failed: status {status.Status}, state {status.State}.", status);
    }

    private static string DescribeCommand(byte code) => code switch
    {
        0x00 => "Get",
        0x21 => "SetAddress",
        0x41 => "Erase",
        0x92 => "ReadUnprotect",
        _ => $"0x{code:X2}",
    };

    private static async Task<int> ProbeTransferSizeAsync(UsbDevice usb, CancellationToken ct)
    {
        try
        {
            var buffer = new byte[256];
            var setup = new UsbControlSetup
            {
                RequestType = 0x80, // device->host, standard, recipient = device
                Request = 0x06,     // GET_DESCRIPTOR
                Value = 0x0200,     // CONFIGURATION descriptor (type 2) << 8 | index 0
                Index = 0,
            };
            int read = await usb.ControlTransferAsync(setup, buffer, ct).ConfigureAwait(false);
            if (DfuFunctionalDescriptor.TryParseTransferSize(buffer.AsSpan(0, read), out int ts) && ts is >= 64 and <= 2048)
                return ts;
        }
        catch
        {
            // fall through to the conservative fallback
        }
        return FallbackTransferSize;
    }

    // AN3156 §1: the bootloader version is the MSB of bcdDevice (e.g. 0x2200 -> "2.2").
    private static string FormatBootloaderVersion(ushort bcdDevice)
        => $"{(bcdDevice >> 12) & 0xF}.{(bcdDevice >> 8) & 0xF}";
}

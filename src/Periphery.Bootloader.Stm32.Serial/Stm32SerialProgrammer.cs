// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.IO.Pipelines;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CallAndResponse;
using CallAndResponse.Protocol.Stm32Bootloader;
using Microsoft.Extensions.Logging;
using Periphery.Firmware;
using CnrSerial = CallAndResponse.Transport.Serial;
using RjcpPorts = RJCP.IO.Ports;

namespace Periphery.Bootloader.Stm32.Serial;

/// <summary>
/// The imperative shell (ADR-0052): drives the AN3155 UART bootloader protocol over an
/// <see cref="IDuplexPipe"/> to identify and flash an STM32 already in system-bootloader mode.
/// Owns the port handle and all timing. Implements the platform contract
/// <see cref="IFirmwareProgrammer"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The transport is a constructor argument.</b> The protocol needs a byte stream and nothing
/// more, so it takes an <see cref="IDuplexPipe"/>. <see cref="OpenAsync"/> is the convenience path
/// that builds one over a real port; anything else that produces a pipe — a Periphery.Serial
/// backend, a BCL SerialPort, a loopback fake — works through the public constructor with no
/// change here.
/// </para>
/// <para>
/// <b>Scope.</b> Flash a device already in system-bootloader mode (BOOT0 asserted, or entered by a
/// vendor command): Extended Erase + image write + read-back verify + Go. Write Protect, Readout
/// Protect, and the option-byte commands are not wired.
/// </para>
/// </remarks>
public sealed class Stm32SerialProgrammer : IFirmwareProgrammer
{
    private const byte Ack = 0x79;

    private readonly Stm32BootloaderClient _client;
    private readonly ITransceiver _transceiver;
    private readonly IDuplexPipe _pipe;
    private readonly Stm32SerialOptions _options;
    private readonly IAsyncDisposable? _pipeOwner;  // the SerialDuplexPipe read pump, when we made it
    private readonly IDisposable? _portOwner;       // the serial port, when we opened it

    /// <summary>
    /// Wraps an already-open byte stream. The caller owns <paramref name="pipe"/> and the transport
    /// beneath it; <see cref="DisposeAsync"/> does not touch either.
    /// </summary>
    /// <param name="device">The discovery snapshot this programmer is for.</param>
    /// <param name="pipe">An active duplex pipe to the bootloader, framed 8E1 at an agreed rate.</param>
    /// <param name="options">Wire and timing settings; <see cref="Stm32SerialOptions.Default"/> when null.</param>
    /// <param name="logger">Optional logger for the underlying transceiver.</param>
    public Stm32SerialProgrammer(
        DeviceInfo device,
        IDuplexPipe pipe,
        Stm32SerialOptions? options = null,
        ILogger<Transceiver>? logger = null)
        : this(device, pipe, options, logger, pipeOwner: null, portOwner: null)
    {
    }

    private Stm32SerialProgrammer(
        DeviceInfo device,
        IDuplexPipe pipe,
        Stm32SerialOptions? options,
        ILogger<Transceiver>? logger,
        IAsyncDisposable? pipeOwner,
        IDisposable? portOwner)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(pipe);

        Device = device;
        _options = options ?? Stm32SerialOptions.Default;
        _pipe = pipe;
        _transceiver = pipe.AsTransceiver(logger);
        _client = new Stm32BootloaderClient(_transceiver);
        _pipeOwner = pipeOwner;
        _portOwner = portOwner;
    }

    /// <inheritdoc />
    public DeviceInfo Device { get; }

    private static readonly ImmutableArray<FirmwareFormat> s_acceptedFormats =
        ImmutableArray.Create(FirmwareFormat.IntelHex, FirmwareFormat.RawBinary, FirmwareFormat.Elf);

    /// <inheritdoc />
    public ImmutableArray<FirmwareFormat> AcceptedFormats => s_acceptedFormats;

    /// <summary>
    /// Opens the device's serial port at 8E1, starts the read pump, and completes the AN3155
    /// autobaud handshake. The device must already be in system-bootloader mode.
    /// </summary>
    /// <exception cref="Stm32SerialException">
    /// The device carries no <see cref="DeviceInfo.PortName"/>, the port cannot be opened, or
    /// nothing answered the sync byte.
    /// </exception>
    public static async Task<Stm32SerialProgrammer> OpenAsync(
        DeviceInfo device,
        Stm32SerialOptions? options = null,
        ILogger<Transceiver>? logger = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        var opts = options ?? Stm32SerialOptions.Default;

        if (device.PortName is not { } portName)
            throw new Stm32SerialException(
                $"device '{device.Name ?? device.Id.Value}' has no serial port name, so there is nothing to open.");

        // Construction, configuration and Open all inside the guard: a rejected property value
        // fails the same way a refused Open does, and the half-built port is still disposed.
        RjcpPorts.SerialPortStream? port = null;
        try
        {
            // AN3155 section 2: 8 data bits, even parity, 1 stop bit. Not configurable.
            port = new RjcpPorts.SerialPortStream(portName.Value)
            {
                BaudRate = opts.BaudRate,
                DataBits = 8,
                Parity = RjcpPorts.Parity.Even,
                StopBits = RjcpPorts.StopBits.One,
            };
            port.Open();
        }
        catch (Exception ex)
        {
            port?.Dispose();
            throw new Stm32SerialException($"could not open {portName.Value}: {ex.Message}", ex);
        }

        var pipe = new CnrSerial.SerialDuplexPipe(port);
        var programmer = new Stm32SerialProgrammer(device, pipe, opts, logger, pipeOwner: pipe, portOwner: port);
        try
        {
            await programmer.SyncAsync(ct).ConfigureAwait(false);
            return programmer;
        }
        catch
        {
            await programmer.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<DeviceIdentity> IdentifyAsync(CancellationToken ct = default)
    {
        string? version = null;
        var commands = ImmutableArray<string>.Empty;

        // Get (0x00) reports the protocol version and command list. Informational, like the DFU
        // sibling's Get: a device that refuses it is still flashable, so a failure is not fatal.
        try
        {
            var info = await WithTimeout(_options.CommandTimeout, ct, t => _client.GetSupportedCommands(t))
                .ConfigureAwait(false);
            version = $"{(info.ProtocolVersion >> 4) & 0xF}.{info.ProtocolVersion & 0xF}";
            commands = info.SupportedCommands.Select(c => c.ToString()).ToImmutableArray();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // leave version / commands unpopulated
        }

        uint? chipId = await TryGetChipIdAsync(ct).ConfigureAwait(false);

        return new DeviceIdentity(
            Family: "STM32",
            Chip: chipId is { } pid ? $"0x{pid:X3}" : null,   // the PID, not a resolved part number (phase 2)
            BootloaderVersion: version,
            TransferSize: _options.WriteChunkSize,
            Regions: ImmutableArray<MemoryRegion>.Empty,
            SupportedCommands: commands);
    }

    /// <inheritdoc />
    public async Task<FlashResult> FlashAsync(
        FirmwarePayload payload,
        FlashOptions options,
        IProgress<FlashProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(options);

        // Safety gate: AN3155 writes addressed bytes, so it flashes only Kind-1 memory images.
        if (!s_acceptedFormats.Contains(payload.Format) || payload.MemoryImage is not { } image)
            return FlashResult.Fail(
                $"STM32 UART cannot flash {payload.Format}; it accepts {string.Join(", ", s_acceptedFormats)}.");

        // AN3155 mass erase is Extended Erase with the 0xFFFF special code. The CallAndResponse
        // client always builds an explicit page list, so it cannot express it. Refusing is better
        // than silently doing a page erase the caller did not ask for.
        if (options.Erase == EraseMode.Mass)
            return FlashResult.Fail(
                "STM32 UART mass erase is not available: the AN3155 client sends an explicit page list, " +
                "not the 0xFFFF mass-erase code. Use EraseMode.Auto or PerPage to erase the pages the " +
                "image covers, or EraseMode.None.");

        try
        {
            var steps = Stm32SerialPlan.Plan(image, _options, options);
            long total = image.TotalBytes;
            long done = 0;

            foreach (var step in steps)
            {
                ct.ThrowIfCancellationRequested();
                switch (step)
                {
                    case Stm32SerialStep.ErasePages erase:
                        progress?.Report(new FlashProgress(
                            FlashPhase.Erasing, 0, total, $"Extended erase, {erase.PageCount} page(s)"));
                        // The client's parameter is the AN3155 half-word N, which erases pages 0..N —
                        // one fewer than the count. The planner never emits a zero count.
                        await WithTimeout(_options.EraseTimeout, ct,
                            t => _client.ExtendedEraseMemoryPages((ushort)(erase.PageCount - 1), t))
                            .ConfigureAwait(false);
                        break;

                    case Stm32SerialStep.Write write:
                        await WithTimeout(_options.CommandTimeout, ct,
                            t => _client.WriteMemory(write.Data, write.Address, t))
                            .ConfigureAwait(false);
                        done += write.Data.Length;
                        progress?.Report(new FlashProgress(FlashPhase.Writing, done, total));
                        break;

                    case Stm32SerialStep.Verify verify:
                        progress?.Report(new FlashProgress(FlashPhase.Verifying, done, total, "Verify"));
                        await VerifySegmentAsync(verify.Address, verify.Expected, ct).ConfigureAwait(false);
                        break;

                    case Stm32SerialStep.Go go:
                        progress?.Report(new FlashProgress(FlashPhase.Leaving, total, total));
                        await GoAsync(go.JumpAddress, ct).ConfigureAwait(false);
                        break;
                }
            }

            progress?.Report(new FlashProgress(FlashPhase.Done, total, total));
            return FlashResult.Ok(total, verified: options.Verify);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Stm32SerialException ex)
        {
            return FlashResult.Fail(ex);
        }
        catch (OperationCanceledException ex)
        {
            // Not the caller's token, so it is one of our command timeouts firing.
            return FlashResult.Fail(new Stm32SerialException(
                "the bootloader stopped answering (command timed out).", ex));
        }
    }

    /// <inheritdoc />
    public Task LeaveAsync(CancellationToken ct = default) => GoAsync(Stm32SerialPlan.FlashBase, ct);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        // Order matters: stop the read pump before closing the port beneath it.
        if (_pipeOwner is not null)
            await _pipeOwner.DisposeAsync().ConfigureAwait(false);
        _portOwner?.Dispose();
    }

    // ── shell helpers (own the timing, ADR-0052 DEC-004) ──

    private async Task SyncAsync(CancellationToken ct)
    {
        // AN3155 section 3.1: 0x7F at 8E1 drives the bootloader's autobaud and it answers ACK. A
        // device that already synced since reset answers NACK instead — also a live bootloader, so
        // both are success. Only silence (our timeout) or a junk byte is a failure.
        try
        {
            await WithTimeout(_options.CommandTimeout, ct, t => _client.Ping(t)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new Stm32SerialException(
                "no valid answer to the AN3155 sync byte (0x7F). Check that the part is in " +
                "system-bootloader mode (BOOT0 asserted and reset), that the port is the right one, " +
                "and that RX/TX are not swapped.", ex);
        }
    }

    // Stm32BootloaderClient.GetId returns byte [4] of the reply, which is the trailing ACK rather
    // than the id. AN3155 section 3.3 answers ACK, N=1, PID_MSB, PID_LSB, ACK — the id is bytes
    // [2..3]. Read it off the transceiver directly until the upstream accessor is fixed.
    private async Task<uint?> TryGetChipIdAsync(CancellationToken ct)
    {
        try
        {
            var reply = await WithTimeout(_options.CommandTimeout, ct,
                t => _transceiver.SendReceiveExactly(new byte[] { 0x02, 0xFD }, 5, t))
                .ConfigureAwait(false);

            if (reply.Length < 5 || reply.Span[0] != Ack)
                return null;
            return (uint)((reply.Span[2] << 8) | reply.Span[3]);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    // Read the segment back with Read Memory and compare to what we wrote. A mismatch throws,
    // which aborts the flash before Go — so a bad image is never started and the part stays in
    // the bootloader for a re-flash.
    private async Task VerifySegmentAsync(uint address, ReadOnlyMemory<byte> expected, CancellationToken ct)
    {
        int verified = 0;
        while (verified < expected.Length)
        {
            ct.ThrowIfCancellationRequested();
            int want = Math.Min(_options.WriteChunkSize, expected.Length - verified);
            uint at = address + (uint)verified;

            var read = await WithTimeout(_options.CommandTimeout, ct,
                t => _client.ReadMemory(at, (uint)want, t))
                .ConfigureAwait(false);

            if (read.Length < want)
                throw new Stm32SerialException(
                    $"verify: short read-back at 0x{at:X8} (got {read.Length} of {want} bytes).");

            // Span compare in a sync helper — an async method cannot hold a ref struct across an await.
            VerifyChunk(read, want, expected, verified, address);

            verified += want;
        }
    }

    private static void VerifyChunk(
        ReadOnlyMemory<byte> readBack, int length, ReadOnlyMemory<byte> expected, int offset, uint baseAddress)
    {
        var got = readBack.Span.Slice(0, length);
        var exp = expected.Span.Slice(offset, length);
        if (got.SequenceEqual(exp))
            return;

        int off = 0;
        while (off < length && got[off] == exp[off]) off++;
        uint at = baseAddress + (uint)offset + (uint)off;
        throw new Stm32SerialException(
            $"verify FAILED at 0x{at:X8}: flash has 0x{got[off]:X2} but the image has 0x{exp[off]:X2} " +
            "(read-back mismatch — the write did not land correctly).");
    }

    // AN3155 section 3.6 is two round trips: the command is ACKed, then the address frame is ACKed
    // and the part jumps. Driven as two steps rather than through the client's Go so the two
    // outcomes can be told apart — a refusal of the command is a real failure, while silence after
    // the address frame is what a successful jump looks like. Swallowing both, as this did, reports
    // a device still sitting in the bootloader as a started application.
    private async Task GoAsync(uint jumpAddress, CancellationToken ct)
    {
        try
        {
            await WithTimeout(_options.CommandTimeout, ct,
                t => _transceiver.SendReceivePerfectMatch(
                    new byte[] { (byte)Stm32BootloaderCommand.Go, 0xDE }, new byte[] { Ack }, t))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new Stm32SerialException(
                "the bootloader refused Go — no ACK to the command. Anything already written is " +
                "intact, but the device is still in the bootloader and the application was not " +
                "started. Read protection and an invalid jump address both cause this.", ex);
        }

        var address = AddressFrame(jumpAddress);

        try
        {
            await WithTimeout(_options.CommandTimeout, ct,
                t => _transceiver.SendReceivePerfectMatch(address, new byte[] { Ack }, t))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // The part jumped to the application — nothing left to answer. Expected.
        }
    }

    // AN3155 addresses are big-endian and carry an XOR checksum byte. Built in a sync helper
    // because the net8.0 target compiles as C# 12, which does not allow a Span local in an async
    // method.
    private static byte[] AddressFrame(uint address)
    {
        var frame = new byte[5];
        BinaryPrimitives.WriteUInt32BigEndian(frame, address);
        frame[4] = (byte)(frame[0] ^ frame[1] ^ frame[2] ^ frame[3]);
        return frame;
    }

    // Every command goes through here, and the three things it does are all required.
    //
    // The deadline: the transceiver waits for a frame indefinitely, so without one a device that
    // stops answering hangs the flash rather than failing it.
    //
    // The drain: Transceiver.ReceiveMessage advances the pipe past the detected payload only, so
    // any bytes after it stay buffered — an AN3155 reply's trailing ACK is exactly that. The next
    // command then starts against a dirty buffer, and SendReceivePerfectMatch scans the whole
    // accumulated buffer for its match, so it would satisfy itself on the stale ACK and return
    // before the device had answered at all. Discarding what is buffered before each command is
    // what keeps a reply matched to its own command.
    //
    // The conversion: this is the boundary where a foreign failure becomes a Stm32SerialException,
    // so callers can catch one type instead of enumerating the set the transport and protocol
    // libraries happen to throw. Enumerating is how a mid-flash unplug escaped FlashAsync
    // uncaught — TransceiverTransportException derives straight from Exception and was not on the
    // list. Converting here means every command gets it, not just the ones someone remembered.
    private async Task<T> WithTimeout<T>(TimeSpan timeout, CancellationToken ct, Func<CancellationToken, Task<T>> action)
    {
        DrainStaleBytes();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            return await action(cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (Convertible(ex))
        {
            throw Convert(ex);
        }
    }

    private async Task WithTimeout(TimeSpan timeout, CancellationToken ct, Func<CancellationToken, Task> action)
    {
        DrainStaleBytes();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await action(cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (Convertible(ex))
        {
            throw Convert(ex);
        }
    }

    // Cancellation is deliberately not converted: the caller's token and our command deadline are
    // both signalled that way and both are handled as cancellation upstream.
    private static bool Convertible(Exception ex) =>
        ex is TransceiverTransportException or InvalidOperationException or ArgumentException;

    private static Stm32SerialException Convert(Exception ex) => ex switch
    {
        TransceiverTransportException => new Stm32SerialException(
            "the serial transport closed mid-command — the cable was unplugged, or the port was " +
            "closed underneath the flash.", ex),
        _ => new Stm32SerialException($"the bootloader rejected a command: {ex.Message}", ex),
    };

    /// <summary>
    /// Discards whatever is already buffered on the read side — a previous reply's trailing ACK,
    /// a NACK from a refused command, line noise from the moment the port opened. Non-blocking:
    /// <see cref="PipeReader.TryRead"/> never waits for bytes that have not arrived.
    /// </summary>
    private void DrainStaleBytes()
    {
        while (_pipe.Input.TryRead(out var result))
        {
            var buffer = result.Buffer;
            _pipe.Input.AdvanceTo(buffer.End);
            if (buffer.IsEmpty || result.IsCompleted)
                break;
        }
    }
}

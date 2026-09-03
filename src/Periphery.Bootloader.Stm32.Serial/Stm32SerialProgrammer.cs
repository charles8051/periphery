// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CallAndResponse;
using CallAndResponse.Protocol.Stm32Bootloader;
using Microsoft.Extensions.Logging;
using Periphery.Firmware;
using Periphery.Serial;
using Bcl = System.IO.Ports;

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
    private const byte Nack = 0x1F;

    /// <summary>AN3155 section 3.1 autobaud byte. Also, to an already-synced part, an opcode.</summary>
    private const byte SyncByte = 0x7F;

    /// <summary>How many times the handshake will clear a byte and re-test with Get.</summary>
    private const int SyncProofAttempts = 3;

    /// <summary>
    /// The rate a part ends up at when its autobaud measured one of our recovery bytes instead of
    /// the sync byte, as a fraction of the rate we are actually driving: 8/9.
    /// </summary>
    /// <remarks>
    /// AN3155 §3.1 has the bootloader derive the bit period from 0x7F, whose framing puts the
    /// second falling edge exactly 8 bit times after the first (start low, D0..D6 high, D7 low).
    /// It divides the span it measures by 8. Send 0xFF instead and the next falling edge is the
    /// <b>parity</b> bit, 9 bit times out, so the part computes a bit period 9/8 too long and
    /// locks to 8/9 of the intended rate — 102400 when we are driving 115200.
    /// <para>
    /// That matters because the recovery path below deliberately sends 0xFF, and a part that
    /// resets while one is in flight autobauds on it. Autobaud happens once per reset, so the
    /// part is then stuck: it answers every command correctly, at a rate ~12% off ours, which
    /// arrives as parity and framing errors. Measured on an STM32G431 (bootloader 3.1) whose
    /// replies decoded byte-perfect at 102400 while reading as noise at 115200.
    /// </para>
    /// </remarks>
    private const int MislockNumerator = 8;

    /// <inheritdoc cref="MislockNumerator" />
    private const int MislockDenominator = 9;

    private readonly Stm32BootloaderClient _client;
    private readonly ITransceiver _transceiver;
    private readonly IDuplexPipe _pipe;
    private readonly Stm32SerialOptions _options;
    private readonly IAsyncDisposable? _pipeOwner;  // the SerialDuplexPipe read pump, when we made it
    private readonly IDisposable? _portOwner;       // the serial port, when we opened it
    private readonly Action<int>? _setBaudRate;     // retunes the port, when the caller owns one
    private bool _sawBytes;                        // anything at all arrived during the handshake

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
        IDisposable? portOwner,
        Action<int>? setBaudRate = null)
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
        _setBaudRate = setBaudRate;
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
        Bcl.SerialPort? port = null;
        try
        {
            // AN3155 section 2: 8 data bits, even parity, 1 stop bit. Not configurable.
            port = new Bcl.SerialPort(portName.Value)
            {
                BaudRate = opts.BaudRate,
                DataBits = 8,
                Parity = Bcl.Parity.Even,
                StopBits = Bcl.StopBits.One,
            };
            port.Open();
        }
        catch (Exception ex)
        {
            port?.Dispose();
            throw new Stm32SerialException($"could not open {portName.Value}: {ex.Message}", ex);
        }

        var pipe = new BclSerialDuplexPipe(port);
        // We opened the port, so we can retune it. That is what lets the handshake tell a part
        // that autobauded wrong apart from one that is not there — see ConfirmMislockAsync.
        var programmer = new Stm32SerialProgrammer(
            device, pipe, opts, logger, pipeOwner: pipe, portOwner: port, setBaudRate: b => port.BaudRate = b);
        return await SyncOrDisposeAsync(programmer, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Completes the AN3155 handshake over a pipe the caller already owns, and returns a
    /// programmer ready to use. The transport is untouched by <see cref="DisposeAsync"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="OpenAsync"/> opens a port for you and owns it. That is wrong for a caller who
    /// has to keep the port across more than one operation — a probe loop holds its handle from
    /// the cycle that detects a target through the flash that follows (autoflash adr.md Decision
    /// 11), and reading a present-detect line for <c>--repeat=cts</c> needs the port object rather
    /// than the pipe over it. Such a caller can already build the programmer through the public
    /// constructor, but that constructor deliberately does not talk to the device, so there was no
    /// way to complete the handshake.
    /// </para>
    /// <para>
    /// On failure the programmer is disposed, which does <i>not</i> close the caller's transport.
    /// Ownership is unchanged: whoever created the pipe still closes it.
    /// </para>
    /// </remarks>
    /// <param name="setBaudRate">
    /// Optional. Retunes the port under <paramref name="pipe"/>. Supply it when you own the port
    /// and the handshake may then distinguish a part that autobauded to the wrong rate from one
    /// that is absent, rather than reporting both as silence. Without it that check is skipped;
    /// nothing else changes. See <see cref="MislockNumerator"/>.
    /// </param>
    /// <exception cref="Stm32SerialException">Nothing answered the sync byte.</exception>
    public static async Task<Stm32SerialProgrammer> ConnectAsync(
        DeviceInfo device,
        IDuplexPipe pipe,
        Stm32SerialOptions? options = null,
        ILogger<Transceiver>? logger = null,
        CancellationToken ct = default,
        Action<int>? setBaudRate = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(pipe);

        var programmer = new Stm32SerialProgrammer(
            device, pipe, options, logger, pipeOwner: null, portOwner: null, setBaudRate: setBaudRate);
        return await SyncOrDisposeAsync(programmer, ct).ConfigureAwait(false);
    }

    // Shared by both factories so a synced programmer is the only kind either can hand back, and
    // a handshake failure never leaks a half-built one.
    private static async Task<Stm32SerialProgrammer> SyncOrDisposeAsync(
        Stm32SerialProgrammer programmer, CancellationToken ct)
    {
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

    /// <summary>
    /// The AN3155 handshake. Internal rather than private so the pipe-driven tests can reach it:
    /// it used to be reachable only through <see cref="OpenAsync"/>, which needs a real port, so
    /// nothing covered it and a wrong reading of section 3.1 shipped.
    /// </summary>
    internal async Task SyncAsync(CancellationToken ct)
    {
        // AN3155 section 3.1: 0x7F at 8E1 drives the bootloader's autobaud and it answers ACK —
        // but ONLY on a part that has not synced since reset. Once synced, the bootloader is in
        // its command loop, where 0x7F is an opcode: it takes the byte and waits for the
        // complement that a command frame carries second. It does not answer, and it does not
        // NACK. It waits.
        //
        // That state is the common one, not the exotic one. Anything that opened the port before
        // us — a terminal, a previous flash, the probe loop, an operator checking the port is
        // alive — leaves the part synced, and our sync byte becomes half a command frame.
        byte? answer = await TrySyncByteAsync(ct).ConfigureAwait(false);

        // A byte we actually received is unambiguous. ACK is a fresh autobaud sync; NACK means
        // the part was already in its command loop and our byte completed an earlier partial
        // frame. Both leave a live bootloader on a known command boundary.
        if (answer is Ack or Nack)
            return;

        if (answer is { } junk)
            throw new Stm32SerialException(
                $"the AN3155 sync byte (0x7F) was answered with 0x{junk:X2}, which is neither ACK " +
                $"(0x{Ack:X2}) nor NACK (0x{Nack:X2}). Check the baud rate and that 8E1 framing is " +
                "reaching the part.");

        // Silence, and silence is where inference gets dangerous. It usually means a synced part
        // is holding our sync byte as an opcode — but it can also be a fresh part whose ACK was
        // merely late, and the two demand opposite repairs. Guessing from the next single byte
        // cannot tell them apart: a late ACK arriving after the deadline reads exactly like a
        // reply to whatever we sent next, so we would report a clean session while a byte sat
        // pending and desynchronise on the following command.
        //
        // So stop inferring. Push a byte to complete any frame the part is holding, then PROVE
        // the boundary with Get, whose reply is long and structured enough that a desynchronised
        // stream cannot fake it. Whether a late ACK was consumed or discarded along the way stops
        // mattering: the proof is what decides, not the guess.
        // Quiesce first. Our deadline cancelled a read; it did not stop the part talking. A late
        // answer, or the tail of a reply longer than the one byte we asked for, can still be on
        // the wire — and a recovery byte sent into that arrives interleaved, which is how a
        // recoverable timeout becomes a desynchronised session. Wait, then drain, then act.
        if (!await SettleAsync(ct).ConfigureAwait(false))
            throw new Stm32SerialException(
                $"the line never fell quiet for {_options.SyncSettle.TotalMilliseconds:F0} ms " +
                $"within {_options.SyncSettleBudget.TotalSeconds:F1} s, so the handshake could not " +
                "establish a command boundary to recover from. Something is transmitting " +
                "continuously — check the baud rate, and that the port is not shared with another " +
                "program or wired to a device that talks unprompted.");

        await NudgeAsync(unchecked((byte)~SyncByte), ct).ConfigureAwait(false);

        if (await ProveCommandBoundaryAsync(ct).ConfigureAwait(false))
            return;

        // Nothing intelligible came back — but "nothing intelligible" is not the same as
        // "nothing there", and the difference decides what the operator should go and do.
        if (await ConfirmMislockAsync(ct).ConfigureAwait(false) is { } lockedBaud)
            throw new Stm32SerialException(
                $"the bootloader is alive but autobauded to {lockedBaud} instead of " +
                $"{_options.BaudRate}: it answered cleanly at {lockedBaud} after saying nothing " +
                $"intelligible at {_options.BaudRate}. A part measures its rate from the first " +
                "byte it sees after reset, so one that came out of reset while a recovery byte " +
                "(0xFF) was on the line locks to 8/9 of the intended rate and stays there. " +
                "Nothing on the host can retune it — reset the part (or power-cycle it) so its " +
                "next autobaud measures the 0x7F sync byte, then flash again.");

        throw new Stm32SerialException(
            "no answer to the AN3155 sync byte (0x7F), and the bootloader did not answer Get " +
            $"after {SyncProofAttempts} attempts to clear the command boundary. Check that the " +
            "part is in system-bootloader mode (BOOT0 asserted and reset), that the port is the " +
            "right one, and that RX/TX are not swapped.");
    }

    /// <summary>
    /// Nudges and re-tests with Get until the part answers one, or the attempts run out.
    /// </summary>
    /// <remarks>
    /// Each failed Get leaves at most one byte pending (its first byte completes the stray frame,
    /// its second becomes the next stray), so one nudge per retry converges. Settling between
    /// attempts matters for the same reason it does on the way in: a Get that timed out may still
    /// deliver its full multi-byte reply afterwards, and those bytes must be cleared rather than
    /// read as the next attempt's answer.
    /// </remarks>
    private async Task<bool> ProveCommandBoundaryAsync(CancellationToken ct)
    {
        for (int attempt = 0; attempt < SyncProofAttempts; attempt++)
        {
            if (await RespondsToGetAsync(ct).ConfigureAwait(false))
                return true;

            if (!await SettleAsync(ct).ConfigureAwait(false))
                return false;

            await NudgeAsync(0xFF, ct).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>
    /// Asks whether a part that said nothing intelligible at our rate is in fact answering
    /// perfectly at the rate a mis-measured autobaud would have left it on. Returns that rate
    /// when it is, and <see langword="null"/> otherwise. The port is always left as it was found.
    /// </summary>
    /// <remarks>
    /// This is a proof, not an inference: it retunes the port and re-runs the same Get that just
    /// failed. A part that answers a well-formed Get at 8/9 of our rate is not a coincidence of
    /// noise — a desynchronised stream does not produce ACK, a length, a version, a command list
    /// and a trailing ACK by chance, which is the same reasoning
    /// <see cref="RespondsToGetAsync"/> already rests on.
    /// <para>
    /// Skipped when the caller did not supply a way to retune, which is the honest outcome: the
    /// pipe alone cannot change the rate, so the check cannot be run and the generic failure
    /// stands.
    /// </para>
    /// </remarks>
    private async Task<int?> ConfirmMislockAsync(CancellationToken ct)
    {
        if (_setBaudRate is not { } retune)
            return null;

        // Only worth asking when something was actually talking. A mis-locked part is not quiet —
        // it answers everything, and its answers arrive as bytes that fail to parse. A port with
        // nothing on it delivers no bytes at all, and that is the common case in a probe loop
        // watching an empty fixture: retuning and re-proving there would buy nothing and cost
        // seconds on every cycle.
        if (!_sawBytes)
            return null;

        int intended = _options.BaudRate;
        int suspect = intended * MislockNumerator / MislockDenominator;
        if (suspect <= 0 || suspect == intended)
            return null;

        try
        {
            retune(suspect);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ArgumentException
                                      or ObjectDisposedException or UnauthorizedAccessException)
        {
            // The port cannot take the rate, or is already gone. Nothing to conclude.
            return null;
        }

        try
        {
            // The retune itself garbles whatever was mid-flight, and everything we sent at the
            // wrong rate landed on the part as noise that may have left a frame half-open. So
            // clear the line, then prove the boundary the same way the primary path does —
            // anything less would report a mis-locked part as absent on the first stray byte.
            await SettleAsync(ct).ConfigureAwait(false);
            return await ProveCommandBoundaryAsync(ct).ConfigureAwait(false) ? suspect : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Stm32SerialException)
        {
            // The port died while we were asking. The caller's original failure is the better
            // report, so fall through to it rather than dressing this up as a diagnosis.
            return null;
        }
        finally
        {
            // Unconditional: the caller owns this port and did not ask us to leave it retuned.
            try { retune(intended); }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or ArgumentException
                                          or ObjectDisposedException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// Sends the sync byte and returns what came back, or <see langword="null"/> on silence.
    /// Only a byte actually received is reported; a deadline is not an answer.
    /// </summary>
    private async Task<byte?> TrySyncByteAsync(CancellationToken ct)
    {
        try
        {
            var reply = await WithTimeout(_options.SyncTimeout, ct,
                t => _transceiver.SendReceiveExactly(new byte[] { SyncByte }, 1, t)).ConfigureAwait(false);
            _sawBytes = true;
            return reply.Span[0];
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // Our own deadline: the part said nothing. That is the one case recovery is for.
            return null;
        }
        // Everything else is the transport failing, not the part staying quiet — a closed port, a
        // pulled cable. Swallowing it as silence would send recovery bytes into a dead stream and
        // report the part missing when the real fault is the connection.
    }

    /// <summary>
    /// Lets the line fall quiet, then clears it. Used only on the handshake's recovery path,
    /// where a cancelled read may leave the part still talking.
    /// </summary>
    /// <returns><see langword="true"/> if a full window passed with nothing arriving.</returns>
    private async Task<bool> SettleAsync(CancellationToken ct)
    {
        DrainStaleBytes();
        if (_options.SyncSettle <= TimeSpan.Zero)
            return true;

        // Drain until a whole window passes with nothing arriving, rather than draining once
        // after a fixed wait. Waiting a fixed interval and then draining assumes the line is
        // quiet by then, which no part of AN3155 promises: a reply delayed between its own bytes
        // puts its head in front of that single drain and its tail behind it, and the tail is
        // then read as the answer to whatever we send next. An idle window is evidence; an
        // elapsed interval is an assumption.
        var spent = System.Diagnostics.Stopwatch.StartNew();
        while (spent.Elapsed < _options.SyncSettleBudget)
        {
            await Task.Delay(_options.SyncSettle, ct).ConfigureAwait(false);
            if (!DrainStaleBytes())
                return true;
        }

        // Budget exhausted with the line still talking. Do not transmit into that: a recovery
        // byte sent while bytes are still arriving is the very interleaving this method exists to
        // prevent, and no answer we got back could be attributed to it. Report instead.
        return false;
    }

    /// <summary>
    /// Sends one byte to complete whatever half-frame the part may be holding, and swallows the
    /// answer. The value is never a real command by accident: a pending opcode <c>X</c> forms a
    /// valid frame only with <c>~X</c>, so <c>0xFF</c> can only ever complete <c>0x00</c> — Get,
    /// which is read-only — and every other pairing fails its checksum and is NACKed.
    /// </summary>
    private async Task NudgeAsync(byte value, CancellationToken ct)
    {
        try
        {
            await WithTimeout(_options.SyncTimeout, ct,
                t => _transceiver.SendReceiveExactly(new byte[] { value }, 1, t)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Stm32SerialException ex) when (IsTransportFailure(ex))
        {
            throw;   // a dead port is not a quiet one
        }
        catch
        {
            // Silence here proves nothing either way; the Get that follows is the test.
        }
    }

    /// <summary>
    /// Whether the part answers Get with a well-formed reply. This is the synchronisation proof:
    /// ACK, length, version, command list and a trailing ACK is not something a desynchronised
    /// stream produces by chance, and Get changes nothing on the part.
    /// </summary>
    private async Task<bool> RespondsToGetAsync(CancellationToken ct)
    {
        try
        {
            var info = await WithTimeout(_options.SyncTimeout, ct,
                t => _client.GetSupportedCommands(t)).ConfigureAwait(false);
            return info.SupportedCommands.Any();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Stm32SerialException ex) when (IsTransportFailure(ex))
        {
            // The port died mid-proof. Retrying cannot help and reporting "the bootloader did not
            // answer Get" would blame the part for a broken connection.
            throw;
        }
        catch
        {
            return false;
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

        // The address stage answers before it jumps, so its reply is read as one byte rather than
        // waited on for an ACK. That distinction is the whole point: a NACK and a silent part are
        // different outcomes, and matching on ACK alone collapses them into "no ACK arrived",
        // which is how a refused jump used to be reported as a started application.
        var address = AddressFrame(jumpAddress);
        byte? reply = null;
        try
        {
            var answer = await WithTimeout(_options.CommandTimeout, ct,
                t => _transceiver.SendReceiveExactly(address, 1, t))
                .ConfigureAwait(false);
            if (answer.Length > 0)
                reply = answer.Span[0];
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // Our own deadline: the part said nothing. AN3155 has the ACK precede the jump, so in
            // principle it should always arrive — but a part that resets promptly, or a USB-serial
            // bridge that drops the byte as the line settles, loses it. Silence is not evidence of
            // refusal, and failing a flash that actually succeeded is the worse error.
            //
            // Only the deadline is caught. A Stm32SerialException here is a converted transport
            // failure — the cable came out after the command was ACKed — and that is not silence:
            // the part's post-Go state is unknown, so it propagates and fails the flash.
        }

        if (reply == Nack)
            throw new Stm32SerialException(
                $"the bootloader refused the jump address 0x{jumpAddress:X8} (NACK). Anything already " +
                "written is intact, but the device is still in the bootloader. The usual cause is no " +
                "valid stack pointer at that address — an image flashed at the wrong base, or an " +
                "erased application region.");
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
    /// <summary>
    /// Whether a converted failure was the transport dying rather than the protocol misbehaving.
    /// <see cref="Convert"/> keeps the original as the inner exception, which is the only thing
    /// separating "the cable came out" from "that reply did not parse" once both are
    /// <see cref="Stm32SerialException"/>. The handshake needs that distinction: a protocol
    /// failure is worth retrying, a dead port is not.
    /// </summary>
    private static bool IsTransportFailure(Stm32SerialException ex) =>
        ex.InnerException is TransceiverTransportException;

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
    /// <returns><see langword="true"/> if anything was discarded.</returns>
    private bool DrainStaleBytes()
    {
        bool discarded = false;
        while (_pipe.Input.TryRead(out var result))
        {
            var buffer = result.Buffer;
            if (!buffer.IsEmpty)
                discarded = _sawBytes = true;
            _pipe.Input.AdvanceTo(buffer.End);
            if (buffer.IsEmpty || result.IsCompleted)
                break;
        }
        return discarded;
    }
}

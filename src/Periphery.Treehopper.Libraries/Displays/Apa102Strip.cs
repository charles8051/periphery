// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Periphery.Treehopper.Libraries.Displays;

/// <summary>
/// An APA102 (or SK9822) LED strip driven over a <see cref="SpiLease"/>.
/// The shell in the functional-core / imperative-shell split (ADR-0052 DEC-005):
/// it owns the clock, the SPI handle, and the flush loop; all frame-generation
/// logic lives in the pure <see cref="LedAnimation"/> layer.
/// </summary>
/// <remarks>
/// Typical usage:
/// <code>
///   await using var spi  = await board.UseSpiAsync(clockMhz: 4);
///   await using var strip = new Apa102Strip(spi, ledCount: 60);
///   await strip.RunAsync(
///       LedAnimation.Sequence.Create(
///           (new LedAnimation.Blink(Rgb.Green), 12),
///           (new LedAnimation.Solid(Rgb.Green),  1)),
///       tickInterval: TimeSpan.FromMilliseconds(33),
///       ct);
/// </code>
/// </remarks>
public sealed class Apa102Strip : IAsyncDisposable
{
    private readonly SpiLease _spi;
    private LedAnimation _current;
    private bool _disposed;

    /// <summary>
    /// Creates a strip driver.
    /// </summary>
    /// <param name="spi">
    /// The SPI lease to write over. The strip takes ownership for its lifetime;
    /// dispose the strip before the lease (or use the <see cref="SpiLease"/>
    /// that was opened exclusively for this strip).
    /// </param>
    /// <param name="ledCount">Number of LEDs in the chain (≥ 1).</param>
    public Apa102Strip(SpiLease spi, int ledCount)
    {
        ArgumentNullException.ThrowIfNull(spi);
        ArgumentOutOfRangeException.ThrowIfLessThan(ledCount, 1);
        _spi    = spi;
        LedCount = ledCount;
        _current = new LedAnimation.Off();
    }

    /// <summary>Number of LEDs in the chain.</summary>
    public int LedCount { get; }

    /// <summary>The animation currently held in state (updated by each tick).</summary>
    public LedAnimation Current => _current;

    // ── Tick / run ─────────────────────────────────────────────────────

    /// <summary>
    /// Renders the current animation state, flushes it to the strip, then
    /// advances the animation one tick. Serialises through the underlying
    /// <see cref="SpiLease"/>.
    /// </summary>
    public async Task TickAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var frame = _current.Render(LedCount);
        await FlushAsync(frame, ct).ConfigureAwait(false);
        _current = _current.Next();
    }

    /// <summary>
    /// Sets the current animation to <paramref name="animation"/> and runs the
    /// tick loop at <paramref name="tickInterval"/> until
    /// <paramref name="ct"/> is cancelled (or the operation is otherwise
    /// stopped). This is the primary entry point for autonomous animation.
    /// </summary>
    /// <param name="animation">Initial animation state.</param>
    /// <param name="tickInterval">
    /// Time between ticks. Default 250 ms; use 33 ms for ~30 FPS smooth animations.
    /// </param>
    /// <param name="ct">Stops the loop when cancelled.</param>
    public async Task RunAsync(
        LedAnimation animation,
        TimeSpan tickInterval,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _current = animation;
        while (!ct.IsCancellationRequested)
        {
            var frame = _current.Render(LedCount);
            await FlushAsync(frame, ct).ConfigureAwait(false);
            _current = _current.Next();
            await Task.Delay(tickInterval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Immediately pushes a single <see cref="LedFrame"/> to the strip without
    /// advancing any animation state. Use for one-shot "show this frame now"
    /// scenarios.
    /// </summary>
    public Task ShowAsync(LedFrame frame, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return FlushAsync(frame, ct);
    }

    /// <summary>Turns all LEDs off immediately.</summary>
    public Task ClearAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return FlushAsync(LedFrame.Off(LedCount), ct);
    }

    // ── Dispose ────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        // Bound the clear: if the strip's SPI is wedged (e.g. the firmware came up in
        // a bad state and the OUT endpoint has filled), an unbounded flush blocks
        // forever — and a process force-killed mid-transfer leaves the USB endpoint
        // stuck, "bricking" the board until it is physically replugged. The timeout
        // cancels the flush (CancelIoEx) so dispose completes with no pending transfer.
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await ClearAsync(cts.Token).ConfigureAwait(false);
        }
        catch { /* best-effort clear — strip may be wedged or already gone */ }
    }

    // ── Private ────────────────────────────────────────────────────────

    // The Treehopper firmware caps a single SPI transaction at 255 bytes (the
    // length field is one byte). An APA102 frame is a continuous clocked byte
    // stream — the LEDs shift on clock edges, not on time — so splitting it across
    // several transfers is safe (the idle gap between transactions is just paused
    // clock). 252 keeps a margin under the cap; a 60-LED frame is already 248 bytes
    // and a 144-LED frame ~589, so chunking is required for realistic strips.
    private const int MaxSpiTransfer = 252;

    // APA102 over a Treehopper must be driven in SPI mode 1,1 (CPOL=1, CPHA=1) with a
    // transmit-only burst — the exact configuration the upstream Treehopper.Libraries
    // driver uses and that has been run against real hardware. The SpiLease
    // default (mode 0,0, full-duplex) leaves the strip dark on this firmware, so the
    // strip pins the mode itself rather than trusting the lease default.
    private const SpiMode StripSpiMode = SpiMode.Mode11;

    private async Task FlushAsync(LedFrame frame, CancellationToken ct)
    {
        var bytes = Apa102Encoder.Encode(frame);
        for (int offset = 0; offset < bytes.Length; offset += MaxSpiTransfer)
        {
            int len = Math.Min(MaxSpiTransfer, bytes.Length - offset);
            // WriteAsync = transmit-only (BurstTx): the strip never sends data back,
            // which is both correct and faster than a full-duplex transfer.
            await _spi.WriteAsync(bytes.AsMemory(offset, len), mode: StripSpiMode, ct: ct)
                .ConfigureAwait(false);
        }
    }
}

// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Immutable;

namespace Periphery.Treehopper.Libraries.Displays;

/// <summary>
/// Closed union of immutable LED-strip animation states. Each variant is a
/// pure state machine: <see cref="Next"/> advances one tick; <see cref="Render"/>
/// projects the current state to a <see cref="LedFrame"/>. (ADR-0052 DEC-005.)
/// </summary>
/// <remarks>
/// <para>
/// The <c>private protected</c> base constructor closes the union — only the
/// nested sealed records defined here can derive from
/// <see cref="LedAnimation"/>.
/// </para>
/// <para>
/// Typical usage with <see cref="Apa102Strip"/>:
/// <code>
///   var anim = new LedAnimation.Breathe(Rgb.Purple);
///   await strip.RunAsync(anim, tickInterval: TimeSpan.FromMilliseconds(33), ct);
/// </code>
/// The shell (<see cref="Apa102Strip.RunAsync"/>) owns the clock.
/// The animation owns only the step logic.
/// </para>
/// </remarks>
public abstract record LedAnimation
{
    private protected LedAnimation() { }

    /// <summary>
    /// Advances the animation by one tick. Returns the next immutable state.
    /// Pure: no IO, no clock, same input → same output.
    /// </summary>
    public abstract LedAnimation Next();

    /// <summary>
    /// Renders the current state to a <see cref="LedFrame"/> for a strip of
    /// <paramref name="count"/> LEDs. Pure: no side effects.
    /// </summary>
    public abstract LedFrame Render(int count);

    // ── Solid ──────────────────────────────────────────────────────────

    /// <summary>All pixels set to a single steady colour. Never changes.</summary>
    public sealed record Solid(Rgb Color) : LedAnimation
    {
        /// <inheritdoc />
        public override LedAnimation Next() => this;

        /// <inheritdoc />
        public override LedFrame Render(int count) => LedFrame.Solid(count, Color);
    }

    // ── Off ────────────────────────────────────────────────────────────

    /// <summary>All pixels off. Equivalent to <c>Solid(Rgb.Black)</c>, never changes.</summary>
    public sealed record Off() : LedAnimation
    {
        /// <inheritdoc />
        public override LedAnimation Next() => this;

        /// <inheritdoc />
        public override LedFrame Render(int count) => LedFrame.Off(count);
    }

    // ── Blink ──────────────────────────────────────────────────────────

    /// <summary>
    /// Alternates the strip between <see cref="Color"/> and off. Phase wraps
    /// over <c><see cref="OnTicks"/> + <see cref="OffTicks"/></c>.
    /// </summary>
    /// <param name="Color">The on-state colour.</param>
    /// <param name="Phase">Current tick within the on/off cycle (0-based).</param>
    /// <param name="OnTicks">Number of ticks the strip is lit. Default 8.</param>
    /// <param name="OffTicks">Number of ticks the strip is dark. Default 8.</param>
    public sealed record Blink(Rgb Color, int Phase = 0, int OnTicks = 8, int OffTicks = 8) : LedAnimation
    {
        /// <inheritdoc />
        public override LedAnimation Next()
            => this with { Phase = (Phase + 1) % (OnTicks + OffTicks) };

        /// <inheritdoc />
        public override LedFrame Render(int count)
            => LedFrame.Solid(count, Phase < OnTicks ? Color : Rgb.Black);
    }

    // ── Breathe ────────────────────────────────────────────────────────

    /// <summary>
    /// Sinusoidally fades the strip in and out. <see cref="Phase"/> advances
    /// by <see cref="PhaseStep"/> each tick; one full cycle = 2π radians.
    /// </summary>
    /// <param name="Color">Peak colour at full brightness.</param>
    /// <param name="Phase">Current phase angle in radians.</param>
    /// <param name="PhaseStep">Phase advance per tick. Default 0.15 rad (≈42 ticks/cycle).</param>
    /// <param name="MinBrightness">Brightness floor (0–1). Default 0.02 (never fully dark).</param>
    public sealed record Breathe(
        Rgb Color,
        double Phase = 0,
        double PhaseStep = 0.15,
        double MinBrightness = 0.02) : LedAnimation
    {
        /// <inheritdoc />
        public override LedAnimation Next()
            => this with { Phase = (Phase + PhaseStep) % (2 * Math.PI) };

        /// <inheritdoc />
        public override LedFrame Render(int count)
        {
            double brightness = MinBrightness + (1.0 - MinBrightness) * (Math.Sin(Phase) * 0.5 + 0.5);
            return LedFrame.Solid(count, Color.Scale(brightness));
        }
    }

    // ── Chase ──────────────────────────────────────────────────────────

    /// <summary>
    /// A single lit pixel sweeps from LED 0 to LED N−1, wrapping.
    /// </summary>
    /// <param name="Color">The moving pixel's colour.</param>
    /// <param name="Position">Index of the lit pixel (unbounded; wrapped in Render).</param>
    public sealed record Chase(Rgb Color, int Position = 0) : LedAnimation
    {
        /// <inheritdoc />
        public override LedAnimation Next() => this with { Position = Position + 1 };

        /// <inheritdoc />
        public override LedFrame Render(int count)
        {
            if (count == 0) return new LedFrame(ImmutableArray<Rgb>.Empty);
            int pos = ((Position % count) + count) % count;
            var pixels = new Rgb[count];
            pixels[pos] = Color;
            return new LedFrame(ImmutableArray.Create(pixels));
        }
    }

    // ── Comet ──────────────────────────────────────────────────────────

    /// <summary>
    /// A bright head pixel sweeps the strip while leaving a fading trail behind it.
    /// </summary>
    /// <param name="Color">Head colour (full brightness).</param>
    /// <param name="Position">Index of the head pixel (unbounded; wrapped in Render).</param>
    /// <param name="FadeFactor">
    /// Per-pixel fade multiplier (0–1). Smaller values produce a shorter trail.
    /// Default 0.6.
    /// </param>
    public sealed record Comet(Rgb Color, int Position = 0, double FadeFactor = 0.6) : LedAnimation
    {
        /// <inheritdoc />
        public override LedAnimation Next() => this with { Position = Position + 1 };

        /// <inheritdoc />
        public override LedFrame Render(int count)
        {
            if (count == 0) return new LedFrame(ImmutableArray<Rgb>.Empty);
            int head = ((Position % count) + count) % count;
            var pixels = new Rgb[count];
            for (int i = 0; i < count; i++)
            {
                int dist = ((head - i + count) % count);
                double fade = Math.Pow(FadeFactor, dist);
                pixels[i] = Color.Scale(fade);
            }
            return new LedFrame(ImmutableArray.Create(pixels));
        }
    }

    // ── Rainbow ────────────────────────────────────────────────────────

    /// <summary>
    /// Distributes hues across the strip and rotates the palette each tick.
    /// </summary>
    /// <param name="HueOffset">Current starting hue (0–360).</param>
    /// <param name="HueStep">
    /// Hue degrees between adjacent pixels. Default 8 (45 pixels/full cycle).
    /// </param>
    /// <param name="Saturation">Colour saturation (0–1). Default 1.0.</param>
    /// <param name="Value">Colour brightness / value (0–1). Default 1.0.</param>
    public sealed record Rainbow(
        double HueOffset = 0,
        double HueStep = 8.0,
        double Saturation = 1.0,
        double Value = 1.0) : LedAnimation
    {
        /// <inheritdoc />
        public override LedAnimation Next()
            => this with { HueOffset = (HueOffset + 1) % 360 };

        /// <inheritdoc />
        public override LedFrame Render(int count)
        {
            var pixels = new Rgb[count];
            for (int i = 0; i < count; i++)
                pixels[i] = Rgb.FromHsv((HueOffset + i * HueStep) % 360, Saturation, Value);
            return new LedFrame(ImmutableArray.Create(pixels));
        }
    }

    // ── Sequence ───────────────────────────────────────────────────────

    /// <summary>
    /// Plays a timeline of inner animations in order. Each non-final step runs
    /// for its tick budget, then advances. The final step ticks its own animation
    /// indefinitely — the "settle and hold." (ADR-0052 DEC-005; mirrors
    /// a downstream application's own sequence-animation type.)
    /// </summary>
    /// <remarks>
    /// <para>
    /// Composition is the whole point: a <see cref="Sequence"/> is itself a
    /// <see cref="LedAnimation"/>, so the engine ticks it exactly like any leaf
    /// variant and timing lives entirely in pure state — no timers, no callbacks,
    /// no lifecycle hooks. "Blink a few times, then hold solid" is just:
    /// <code>
    ///   LedAnimation.Sequence.Create(
    ///       (new LedAnimation.Blink(Rgb.Green), 12),
    ///       (new LedAnimation.Solid(Rgb.Green),  1))   // holds forever
    /// </code>
    /// </para>
    /// <para>
    /// The final step's tick budget is ignored (it holds), but must be supplied.
    /// Every non-final step must have a positive tick count — enforced by
    /// <see cref="Create"/>.
    /// </para>
    /// </remarks>
    public sealed record Sequence(
        ImmutableArray<(LedAnimation Animation, int Ticks)> Steps,
        int StepIndex,
        int TicksRemaining,
        LedAnimation Active) : LedAnimation
    {
        /// <summary>
        /// Builds a sequence from <c>(animation, ticks)</c> pairs. The final
        /// step holds indefinitely; every earlier step needs a positive tick
        /// budget.
        /// </summary>
        public static Sequence Create(params (LedAnimation Animation, int Ticks)[] steps)
        {
            if (steps.Length == 0)
                throw new System.ArgumentException("A sequence needs at least one step.", nameof(steps));
            for (int i = 0; i < steps.Length - 1; i++)
                if (steps[i].Ticks <= 0)
                    throw new System.ArgumentOutOfRangeException(
                        nameof(steps), $"steps[{i}].Ticks must be positive.");
            var arr = ImmutableArray.Create(steps);
            return new Sequence(arr, 0, steps[0].Ticks, steps[0].Animation);
        }

        /// <inheritdoc />
        public override LedAnimation Next()
        {
            var advanced   = Active.Next();
            int remaining  = TicksRemaining - 1;

            // Advance only when budget is spent AND a later step exists.
            // The final step has nothing after it, so it just keeps ticking — the hold.
            if (remaining <= 0 && StepIndex < Steps.Length - 1)
            {
                var next = Steps[StepIndex + 1];
                return this with
                {
                    StepIndex      = StepIndex + 1,
                    TicksRemaining = next.Ticks,
                    Active         = next.Animation, // fresh seed for the new step
                };
            }

            return this with { Active = advanced, TicksRemaining = remaining };
        }

        /// <inheritdoc />
        public override LedFrame Render(int count) => Active.Render(count);
    }
}

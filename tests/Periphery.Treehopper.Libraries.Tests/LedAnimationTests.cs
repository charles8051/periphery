using System;
using System.Collections.Immutable;

namespace Periphery.Treehopper.Libraries.Tests;

/// <summary>
/// Asserts that every <see cref="LedAnimation"/> variant is a correct pure state
/// machine: same input → same output, <see cref="LedAnimation.Next"/> is
/// non-mutating, and <see cref="LedAnimation.Render"/> produces the expected
/// pixel layout. Zero hardware. (ADR-0052 DEC-005.)
/// </summary>
public class LedAnimationTests
{
    private const int N = 8; // strip size used throughout

    // ── Solid ──────────────────────────────────────────────────────────

    [Fact]
    public void Solid_Render_AllPixelsAreColor()
    {
        var anim  = new LedAnimation.Solid(Rgb.Red);
        var frame = anim.Render(N);

        Assert.Equal(N, frame.Count);
        Assert.All(frame.Pixels, px => Assert.Equal(Rgb.Red, px));
    }

    [Fact]
    public void Solid_Next_ReturnsSelf()
    {
        var anim = new LedAnimation.Solid(Rgb.Blue);
        Assert.Same(anim, anim.Next());
    }

    // ── Off ────────────────────────────────────────────────────────────

    [Fact]
    public void Off_Render_AllPixelsAreBlack()
    {
        var frame = new LedAnimation.Off().Render(N);
        Assert.All(frame.Pixels, px => Assert.Equal(Rgb.Black, px));
    }

    // ── Blink ──────────────────────────────────────────────────────────

    [Fact]
    public void Blink_PhaseZero_RendersOnColor()
    {
        var anim  = new LedAnimation.Blink(Rgb.Green, Phase: 0, OnTicks: 4, OffTicks: 4);
        var frame = anim.Render(N);
        Assert.All(frame.Pixels, px => Assert.Equal(Rgb.Green, px));
    }

    [Fact]
    public void Blink_PhaseDuringOff_RendersBlack()
    {
        var anim  = new LedAnimation.Blink(Rgb.Green, Phase: 4, OnTicks: 4, OffTicks: 4);
        var frame = anim.Render(N);
        Assert.All(frame.Pixels, px => Assert.Equal(Rgb.Black, px));
    }

    [Fact]
    public void Blink_Next_AdvancesPhaseAndWraps()
    {
        var anim  = new LedAnimation.Blink(Rgb.Red, Phase: 7, OnTicks: 4, OffTicks: 4);
        var next  = (LedAnimation.Blink)anim.Next();
        Assert.Equal(0, next.Phase); // (7+1) % 8 = 0
    }

    [Fact]
    public void Blink_Immutability_OriginalPhaseUnchanged()
    {
        var anim = new LedAnimation.Blink(Rgb.Red, Phase: 2);
        _ = anim.Next();
        Assert.Equal(2, anim.Phase);
    }

    // ── Breathe ────────────────────────────────────────────────────────

    [Fact]
    public void Breathe_AtPeakPhase_RendersNearFullBrightness()
    {
        // Peak = π/2 (sin = 1.0)
        var anim  = new LedAnimation.Breathe(Rgb.White, Phase: Math.PI / 2, MinBrightness: 0);
        var frame = anim.Render(1);
        // brightness = 0 + 1 * (1.0*0.5 + 0.5) = 1.0 → pixel ≈ White
        Assert.Equal(255, frame.Pixels[0].R);
        Assert.Equal(255, frame.Pixels[0].G);
        Assert.Equal(255, frame.Pixels[0].B);
    }

    [Fact]
    public void Breathe_AtTroughPhase_RendersNearZeroBrightness()
    {
        // Trough = 3π/2 (sin = -1.0)
        var anim  = new LedAnimation.Breathe(Rgb.White, Phase: 3 * Math.PI / 2, MinBrightness: 0);
        var frame = anim.Render(1);
        // brightness = 0 + 1 * (-1*0.5 + 0.5) = 0 → black
        Assert.Equal(0, frame.Pixels[0].R);
    }

    [Fact]
    public void Breathe_Next_AdvancesPhase()
    {
        var anim = new LedAnimation.Breathe(Rgb.Red, Phase: 0, PhaseStep: 0.1);
        var next = (LedAnimation.Breathe)anim.Next();
        Assert.Equal(0.1, next.Phase, precision: 10);
    }

    [Fact]
    public void Breathe_Next_WrapsPhaseAt2Pi()
    {
        double startPhase = 2 * Math.PI - 0.05;
        var anim = new LedAnimation.Breathe(Rgb.Red, Phase: startPhase, PhaseStep: 0.1);
        var next = (LedAnimation.Breathe)anim.Next();
        Assert.True(next.Phase < 0.1, $"Expected phase < 0.1, got {next.Phase}");
    }

    // ── Chase ──────────────────────────────────────────────────────────

    [Fact]
    public void Chase_Render_OnlyPositionPixelIsLit()
    {
        var anim  = new LedAnimation.Chase(Rgb.Yellow, Position: 3);
        var frame = anim.Render(N);

        for (int i = 0; i < N; i++)
            Assert.Equal(i == 3 ? Rgb.Yellow : Rgb.Black, frame.Pixels[i]);
    }

    [Fact]
    public void Chase_Next_IncrementsPosition()
    {
        var anim = new LedAnimation.Chase(Rgb.Red, Position: 2);
        var next = (LedAnimation.Chase)anim.Next();
        Assert.Equal(3, next.Position);
    }

    [Fact]
    public void Chase_Render_PositionWrapsAroundStrip()
    {
        var anim  = new LedAnimation.Chase(Rgb.Cyan, Position: N + 2);
        var frame = anim.Render(N);
        Assert.Equal(Rgb.Cyan, frame.Pixels[2]);
    }

    // ── Comet ──────────────────────────────────────────────────────────

    [Fact]
    public void Comet_Render_HeadIsFullBrightness()
    {
        var anim  = new LedAnimation.Comet(Rgb.White, Position: 0, FadeFactor: 0.5);
        var frame = anim.Render(N);
        Assert.Equal(Rgb.White, frame.Pixels[0]);
    }

    [Fact]
    public void Comet_Render_TrailFadesExponentially()
    {
        var anim  = new LedAnimation.Comet(Rgb.White, Position: 0, FadeFactor: 0.5);
        var frame = anim.Render(N);
        // pixel[N-1] is 1 behind head (wrapping), so scale = 0.5^1 = 0.5 → 128
        // pixel[N-2] is 2 behind head, scale = 0.5^2 = 0.25 → 64
        Assert.Equal((byte)Math.Round(255 * 0.5),  frame.Pixels[N - 1].R);
        Assert.Equal((byte)Math.Round(255 * 0.25), frame.Pixels[N - 2].R);
    }

    // ── Rainbow ────────────────────────────────────────────────────────

    [Fact]
    public void Rainbow_Render_PixelCountMatchesStrip()
    {
        var frame = new LedAnimation.Rainbow().Render(N);
        Assert.Equal(N, frame.Count);
    }

    [Fact]
    public void Rainbow_Next_IncrementsHueOffset()
    {
        var anim = new LedAnimation.Rainbow(HueOffset: 350);
        var next = (LedAnimation.Rainbow)anim.Next();
        Assert.Equal(351, next.HueOffset, precision: 8);
    }

    [Fact]
    public void Rainbow_HueOffset_WrapsAt360()
    {
        var anim = new LedAnimation.Rainbow(HueOffset: 359);
        var next = (LedAnimation.Rainbow)anim.Next();
        Assert.Equal(0, next.HueOffset, precision: 8);
    }

    // ── Rgb.FromHsv ────────────────────────────────────────────────────

    [Theory]
    [InlineData(  0, 1, 1, 255,   0,   0)] // pure red
    [InlineData(120, 1, 1,   0, 255,   0)] // pure green
    [InlineData(240, 1, 1,   0,   0, 255)] // pure blue
    [InlineData(  0, 0, 1, 255, 255, 255)] // white (no saturation)
    [InlineData(  0, 0, 0,   0,   0,   0)] // black (no value)
    public void Rgb_FromHsv_MatchesExpected(double h, double s, double v, int r, int g, int b)
    {
        var rgb = Rgb.FromHsv(h, s, v);
        Assert.Equal((byte)r, rgb.R);
        Assert.Equal((byte)g, rgb.G);
        Assert.Equal((byte)b, rgb.B);
    }

    // ── Sequence — settle-and-hold ─────────────────────────────────────

    [Fact]
    public void Sequence_Create_StartsAtFirstStep()
    {
        var anim = LedAnimation.Sequence.Create(
            (new LedAnimation.Solid(Rgb.Red),   4),
            (new LedAnimation.Solid(Rgb.Green), 1));

        Assert.Equal(0, anim.StepIndex);
        Assert.Equal(4, anim.TicksRemaining);
    }

    [Fact]
    public void Sequence_AdvancesAfterBudgetExpires()
    {
        var anim = LedAnimation.Sequence.Create(
            (new LedAnimation.Solid(Rgb.Red),   2),
            (new LedAnimation.Solid(Rgb.Green), 1));

        var s1 = (LedAnimation.Sequence)anim.Next(); // tick 1 of 2
        var s2 = (LedAnimation.Sequence)s1.Next();   // tick 2 → step 1

        Assert.Equal(0, s1.StepIndex);
        Assert.Equal(1, s2.StepIndex);
    }

    [Fact]
    public void Sequence_FinalStep_HoldsIndefinitely()
    {
        var anim = LedAnimation.Sequence.Create(
            (new LedAnimation.Solid(Rgb.Red),   1),
            (new LedAnimation.Solid(Rgb.Green), 1)); // final step — holds

        // Tick through the first step
        var atFinal = (LedAnimation.Sequence)anim.Next();
        Assert.Equal(1, atFinal.StepIndex);

        // Ticking many more times must not advance past final step
        var state = atFinal;
        for (int i = 0; i < 100; i++)
            state = (LedAnimation.Sequence)state.Next();

        Assert.Equal(1, state.StepIndex); // still on the final step
    }

    [Fact]
    public void Sequence_RendersActiveStep()
    {
        var anim = LedAnimation.Sequence.Create(
            (new LedAnimation.Solid(Rgb.Red),   2),
            (new LedAnimation.Solid(Rgb.Green), 1));

        // Initial state → red
        Assert.Equal(Rgb.Red, anim.Render(1).Pixels[0]);

        // After advancing to step 1 → green
        var atGreen = (LedAnimation.Sequence)anim.Next().Next();
        Assert.Equal(Rgb.Green, atGreen.Render(1).Pixels[0]);
    }

    [Fact]
    public void Sequence_AdvancesInnerAnimation_WhileOnSameStep()
    {
        // Use Blink so we can observe the inner Next() being called
        var blink = new LedAnimation.Blink(Rgb.Red, Phase: 0, OnTicks: 2, OffTicks: 2);
        var seq   = LedAnimation.Sequence.Create((blink, 4), (new LedAnimation.Off(), 1));

        var s1 = (LedAnimation.Sequence)seq.Next(); // inner phase → 1
        var inner = (LedAnimation.Blink)s1.Active;
        Assert.Equal(1, inner.Phase);
    }

    [Fact]
    public void Sequence_FreshSeedOnStepEntry()
    {
        // The blink starts at phase 0 each time we enter step 0 (if we had looping),
        // but in settle-and-hold it only enters each step once.
        // Here we just check the active state is the seed animation for step 1.
        var blink = new LedAnimation.Blink(Rgb.Blue, Phase: 0, OnTicks: 4, OffTicks: 4);
        var seq   = LedAnimation.Sequence.Create(
            (new LedAnimation.Solid(Rgb.Red), 1),
            (blink, 1));

        var atStep1 = (LedAnimation.Sequence)seq.Next();
        Assert.Equal(0, ((LedAnimation.Blink)atStep1.Active).Phase); // fresh seed
    }

    [Fact]
    public void Sequence_Create_EmptySteps_Throws()
        => Assert.Throws<ArgumentException>(() =>
            LedAnimation.Sequence.Create());

    [Fact]
    public void Sequence_Create_NonFinalZeroTicks_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() =>
            LedAnimation.Sequence.Create(
                (new LedAnimation.Solid(Rgb.Red), 0),  // zero budget on non-final
                (new LedAnimation.Solid(Rgb.Green), 1)));

    [Fact]
    public void Sequence_Create_FinalStepZeroTicks_IsAllowed()
    {
        // Final step budget is ignored — zero or any value is fine
        var seq = LedAnimation.Sequence.Create(
            (new LedAnimation.Solid(Rgb.Red),   3),
            (new LedAnimation.Solid(Rgb.Green), 0));  // 0 is OK for final
        Assert.Equal(2, seq.Steps.Length);
    }

    // ── Immutability cross-check ────────────────────────────────────────

    [Fact]
    public void Next_IsNonMutating_OriginalStatePreserved()
    {
        var anim = new LedAnimation.Chase(Rgb.Orange, Position: 5);
        _ = anim.Next();
        Assert.Equal(5, anim.Position); // original is unchanged
    }

    [Fact]
    public void Render_IsPure_TwiceReturnsSameResult()
    {
        var anim  = new LedAnimation.Rainbow(HueOffset: 45);
        var f1    = anim.Render(N);
        var f2    = anim.Render(N);
        Assert.Equal(f1.Pixels, f2.Pixels);
    }
}

using Periphery.Camera;
using Periphery.Camera.Linux;
using Periphery.Camera.Testing;

namespace Periphery.Camera.Tests;

/// <summary>
/// Reading a control's current value and mode — the piece that makes
/// <see cref="CameraDevice.SetControlAsync"/> reversible, because without it a
/// caller can move a control but cannot record where it was.
/// </summary>
[Collection("Camera")]
public sealed class CameraControlStateTests
{
    [Fact]
    public async Task AnUntouchedControlReportsTheDeviceDrivingItself()
    {
        // The state a camera is actually in when nobody has intervened. A fake
        // that started everything Manual would let a consumer's "restore what I
        // found" logic pass without ever meeting the case it exists for.
        using var scope = CameraTestScope.Install(new InMemoryCameraBackend());
        await using var device = await CameraDevice.OpenAsync(CameraTestFormats.CreateDeviceInfo());

        var state = await device.GetControlAsync(CameraControlKind.Exposure);

        Assert.NotNull(state);
        Assert.Equal(CameraControlMode.Automatic, state!.Mode);
    }

    [Fact]
    public async Task SettingAValueTakesTheControlOutOfAutomatic()
    {
        using var scope = CameraTestScope.Install(new InMemoryCameraBackend());
        await using var device = await CameraDevice.OpenAsync(CameraTestFormats.CreateDeviceInfo());

        await device.SetControlAsync(CameraControlKind.Exposure, -7);
        var state = await device.GetControlAsync(CameraControlKind.Exposure);

        Assert.Equal(-7, state!.Value);
        Assert.Equal(CameraControlMode.Manual, state.Mode);
    }

    [Fact]
    public async Task ResettingHandsTheControlBackToTheDevice()
    {
        using var scope = CameraTestScope.Install(new InMemoryCameraBackend());
        await using var device = await CameraDevice.OpenAsync(CameraTestFormats.CreateDeviceInfo());

        await device.SetControlAsync(CameraControlKind.Exposure, -7);
        await device.ResetControlAsync(CameraControlKind.Exposure);
        var state = await device.GetControlAsync(CameraControlKind.Exposure);

        Assert.Equal(CameraControlMode.Automatic, state!.Mode);
    }

    [Fact]
    public async Task ReadingAControlTheDeviceDoesNotHaveIsNotAnError()
    {
        // "This camera has no zoom" is an answer a caller must be able to receive
        // from a query without catching anything.
        using var scope = CameraTestScope.Install(new InMemoryCameraBackend());
        await using var device = await CameraDevice.OpenAsync(CameraTestFormats.CreateDeviceInfo());

        Assert.Null(await device.GetControlAsync(CameraControlKind.Zoom));
    }

    [Fact]
    public async Task AControlWithNoAutomaticModeReadsAsManualNotUnknown()
    {
        // Brightness advertises no auto mode in the fixture, so it is manual by
        // construction. That is a real determination; Unknown is reserved for
        // "there is something to ask and it would not answer".
        using var scope = CameraTestScope.Install(new InMemoryCameraBackend());
        await using var device = await CameraDevice.OpenAsync(CameraTestFormats.CreateDeviceInfo());

        var state = await device.GetControlAsync(CameraControlKind.Brightness);

        Assert.Equal(CameraControlMode.Manual, state!.Mode);
    }

    [Fact]
    public async Task SetThenReadRoundTripsTheValue()
    {
        // The whole point: a caller can record where a control was, move it, and
        // put it back.
        using var scope = CameraTestScope.Install(new InMemoryCameraBackend());
        await using var device = await CameraDevice.OpenAsync(CameraTestFormats.CreateDeviceInfo());

        var before = await device.GetControlAsync(CameraControlKind.Brightness);
        await device.SetControlAsync(CameraControlKind.Brightness, 12);
        await device.SetControlAsync(CameraControlKind.Brightness, before!.Value);

        var after = await device.GetControlAsync(CameraControlKind.Brightness);
        Assert.Equal(before.Value, after!.Value);
    }

    [Fact]
    public async Task UnknownIsReachableThroughTheFake()
    {
        // The member whose documentation warns consumers not to read it as
        // Manual. If a consumer cannot produce it in a test, logic that
        // mishandles it ships green.
        var backend = new InMemoryCameraBackend();
        backend.SetControlState(CameraControlKind.Exposure, -5, CameraControlMode.Unknown);
        using var scope = CameraTestScope.Install(backend);
        await using var device = await CameraDevice.OpenAsync(CameraTestFormats.CreateDeviceInfo());

        var state = await device.GetControlAsync(CameraControlKind.Exposure);

        Assert.Equal(CameraControlMode.Unknown, state!.Mode);
    }

    [Fact]
    public async Task ADriverThatAnswersEnumerationButRefusesAReadIsModelled()
    {
        // The failure mode the Windows backend's unsupported-property branch was
        // written for. Enumeration lists the control; the read declines.
        var backend = new InMemoryCameraBackend();
        backend.RefuseControlRead(CameraControlKind.Exposure);
        using var scope = CameraTestScope.Install(backend);
        await using var device = await CameraDevice.OpenAsync(CameraTestFormats.CreateDeviceInfo());

        Assert.Contains(
            await device.GetControlsAsync(), c => c.Kind == CameraControlKind.Exposure);
        Assert.Null(await device.GetControlAsync(CameraControlKind.Exposure));
    }

    [Fact]
    public void MapModeToAutoValueInvertsInterpretAutoValue()
    {
        // The pair that makes V4L2's SetControlAsync mean what the Windows one
        // means. If they drift apart, a Linux write stops taking the control away
        // from the auto loop and the value is silently overwritten on the next
        // frame.
        foreach (var kind in new[]
                 {
                     CameraControlKind.Exposure, CameraControlKind.WhiteBalance,
                     CameraControlKind.Gain, CameraControlKind.Focus,
                 })
        {
            foreach (var mode in new[] { CameraControlMode.Manual, CameraControlMode.Automatic })
            {
                var written = V4l2FormatMap.MapModeToAutoValue(kind, mode);
                Assert.Equal(mode, V4l2FormatMap.InterpretAutoValue(kind, written));
            }
        }
    }

    // ── the V4L2 auto-companion mapping ──────────────────────────────────
    //
    // Pure, and worth testing directly on Windows because the inconsistency it
    // encodes is the kind that is only ever discovered on the hardware nobody
    // has to hand.

    [Theory]
    [InlineData(0, CameraControlMode.Automatic)]  // V4L2_EXPOSURE_AUTO
    [InlineData(1, CameraControlMode.Manual)]     // V4L2_EXPOSURE_MANUAL
    [InlineData(2, CameraControlMode.Manual)]     // SHUTTER_PRIORITY — time is held
    [InlineData(3, CameraControlMode.Automatic)]  // APERTURE_PRIORITY — time is driven
    public void ExposureAutoIsAnEnumerationRunningTheOppositeWay(int raw, CameraControlMode expected)
    {
        // V4L2_CID_EXPOSURE_AUTO is 0 for automatic and 1 for manual — the
        // reverse of every boolean auto control beside it. Read as a boolean,
        // every auto-exposure camera reports as manual.
        Assert.Equal(expected, V4l2FormatMap.InterpretAutoValue(CameraControlKind.Exposure, raw));
    }

    [Theory]
    [InlineData(CameraControlKind.WhiteBalance)]
    [InlineData(CameraControlKind.Gain)]
    [InlineData(CameraControlKind.Focus)]
    public void TheOtherAutoCompanionsAreOrdinaryBooleans(CameraControlKind kind)
    {
        Assert.Equal(CameraControlMode.Automatic, V4l2FormatMap.InterpretAutoValue(kind, 1));
        Assert.Equal(CameraControlMode.Manual, V4l2FormatMap.InterpretAutoValue(kind, 0));
    }

    [Fact]
    public void AnUnrecognisedExposureModeIsUnknownRatherThanGuessed()
    {
        Assert.Equal(
            CameraControlMode.Unknown,
            V4l2FormatMap.InterpretAutoValue(CameraControlKind.Exposure, 99));
    }

    // ── #275: the menu direction the inverse test never checked ───────

    [Fact]
    public void EveryValueInterpretAutoValueRecognises_IsReachableFromAutoValueCandidates()
    {
        // MapModeToAutoValueInvertsInterpretAutoValue checks Interpret(Map(mode)) == mode, which
        // iterates the TWO CameraControlMode values. That direction cannot see the gap: for
        // Exposure, InterpretAutoValue accepts FOUR menu values, while MapModeToAutoValue could
        // only ever emit two of them. So the read side acknowledged device states the write side
        // could not produce, and ResetControlAsync had no way to return a camera to
        // APERTURE_PRIORITY — or to reach automatic at all on a device advertising only 1 and 3
        // (#275).
        //
        // This is the other direction: every raw value we are willing to READ as a mode must be
        // a value we are willing to WRITE for that mode.
        foreach (int raw in new[]
                 {
                     V4l2Interop.V4L2_EXPOSURE_AUTO_MODE,
                     V4l2Interop.V4L2_EXPOSURE_MANUAL,
                     V4l2Interop.V4L2_EXPOSURE_SHUTTER_PRIORITY,
                     V4l2Interop.V4L2_EXPOSURE_APERTURE_PRIORITY,
                 })
        {
            var mode = V4l2FormatMap.InterpretAutoValue(CameraControlKind.Exposure, raw);
            Assert.NotEqual(CameraControlMode.Unknown, mode);

            Assert.Contains(raw, V4l2FormatMap.AutoValueCandidates(CameraControlKind.Exposure, mode));
        }
    }

    [Fact]
    public void AutoValueCandidates_PrefersTheExactModeOverThePriorityFallback()
    {
        // Order is fidelity, not numeric. A device that advertises plain AUTO must get AUTO, not
        // aperture priority — the fallback exists for devices that offer nothing better.
        Assert.Equal(
            new[] { V4l2Interop.V4L2_EXPOSURE_AUTO_MODE, V4l2Interop.V4L2_EXPOSURE_APERTURE_PRIORITY },
            V4l2FormatMap.AutoValueCandidates(CameraControlKind.Exposure, CameraControlMode.Automatic));

        Assert.Equal(
            new[] { V4l2Interop.V4L2_EXPOSURE_MANUAL, V4l2Interop.V4L2_EXPOSURE_SHUTTER_PRIORITY },
            V4l2FormatMap.AutoValueCandidates(CameraControlKind.Exposure, CameraControlMode.Manual));
    }

    [Theory]
    [InlineData(CameraControlKind.WhiteBalance)]
    [InlineData(CameraControlKind.Gain)]
    [InlineData(CameraControlKind.Focus)]
    public void AutoValueCandidates_LeavesTheBooleanCompanionsAlone(CameraControlKind kind)
    {
        // The boolean companions have exactly one right answer each; introducing a candidate
        // list must not have given them a second one.
        foreach (var mode in new[] { CameraControlMode.Manual, CameraControlMode.Automatic })
        {
            Assert.Equal(
                new[] { V4l2FormatMap.MapModeToAutoValue(kind, mode) },
                V4l2FormatMap.AutoValueCandidates(kind, mode));
        }
    }
}

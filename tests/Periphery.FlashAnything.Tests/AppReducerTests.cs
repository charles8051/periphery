namespace Periphery.FlashAnything.Tests;

/// <summary>The pure core: fold events, assert the resulting immutable state. No IO.</summary>
public class AppReducerTests
{
    [Fact]
    public void Detect_adds_target_and_auto_selects_first()
    {
        var s = AppReducer.Reduce(AppState.Empty, new AppEvent.TargetDetected("a", "Acme", "Fake"));

        Assert.Single(s.Targets);
        Assert.Equal("a", s.SelectedTargetId);
        Assert.Equal("Acme", s.Targets[0].DisplayName);
        Assert.Equal(FlashStage.Detected, s.Targets[0].Stage);
    }

    [Fact]
    public void Identified_marks_ready_with_identity()
    {
        var identity = DeviceIdentity.Unknown("STM32");
        var s = AppReducer.ReduceAll(AppState.Empty,
            new AppEvent.TargetDetected("a", "Acme", "Fake"),
            new AppEvent.TargetIdentified("a", identity));

        Assert.Equal(FlashStage.Ready, s.Find("a")!.Stage);
        Assert.Equal(identity, s.Find("a")!.Identity);
    }

    [Fact]
    public void Progress_then_finish_sets_flashed_at_100()
    {
        var s = AppReducer.ReduceAll(AppState.Empty,
            new AppEvent.TargetDetected("a", "Acme", "Fake"),
            new AppEvent.FlashStarted("a"),
            new AppEvent.FlashProgressed("a", new FlashProgress(FlashPhase.Writing, 5, 10)),
            new AppEvent.FlashFinished("a", FlashResult.Ok(10, verified: true)));

        var t = s.Find("a")!;
        Assert.Equal(FlashStage.Flashed, t.Stage);
        Assert.Equal(100, t.Percent);
        Assert.Null(t.LastError);
    }

    [Fact]
    public void Finish_failure_marks_failed_with_error()
    {
        var s = AppReducer.ReduceAll(AppState.Empty,
            new AppEvent.TargetDetected("a", "Acme", "Fake"),
            new AppEvent.FlashStarted("a"),
            new AppEvent.FlashFinished("a", FlashResult.Fail("boom")));

        var t = s.Find("a")!;
        Assert.Equal(FlashStage.Failed, t.Stage);
        Assert.Equal("boom", t.LastError);
    }

    [Fact]
    public void OperationFailed_surfaces_error_without_marking_failed()
    {
        var s = AppReducer.ReduceAll(AppState.Empty,
            new AppEvent.TargetDetected("a", "Acme", "Fake"),
            new AppEvent.TargetIdentified("a", DeviceIdentity.Unknown("X")),
            new AppEvent.OperationFailed("a", "no firmware"));

        var t = s.Find("a")!;
        Assert.Equal("no firmware", t.LastError);
        Assert.Equal(FlashStage.Ready, t.Stage); // not forced to Failed
    }

    [Fact]
    public void Remove_reselects_remaining_target()
    {
        var s = AppReducer.ReduceAll(AppState.Empty,
            new AppEvent.TargetDetected("a", "A", "Fake"),
            new AppEvent.TargetDetected("b", "B", "Fake"),
            new AppEvent.TargetRemoved("a"));

        Assert.Single(s.Targets);
        Assert.Equal("b", s.SelectedTargetId);
    }
}

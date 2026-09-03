using System.Collections.Immutable;

namespace Periphery.FlashAnything.Tests;

/// <summary>
/// The pure probe-row decision (adr.md Decision 9). A decision table: no IO, no clock.
/// </summary>
public class ProbeRowPolicyTests
{
    private static readonly DeviceIdentity G431 = new(
        Family: "STM32", Chip: "0x468", BootloaderVersion: "3.1",
        TransferSize: 256, Regions: ImmutableArray<MemoryRegion>.Empty,
        SupportedCommands: ImmutableArray<string>.Empty);

    private static ProbeOutcome Answered => new ProbeOutcome.Occupied(G431);
    private static ProbeOutcome Silent => ProbeOutcome.NoResponse.Instance;

    private static ProbeRowState Run(ProbeRowState from, params ProbeOutcome[] outcomes)
    {
        var state = from;
        foreach (var o in outcomes) (state, _) = ProbeRowPolicy.Advance(state, o);
        return state;
    }

    [Fact]
    public void An_answer_reports_the_target_once()
    {
        var (state, action) = ProbeRowPolicy.Advance(ProbeRowState.Initial, Answered);

        Assert.Equal(G431, Assert.IsType<ProbeRowAction.Detected>(action).Identity);
        Assert.True(state.Occupied);
        Assert.True(state.Reported);
    }

    [Fact]
    public void A_board_that_stays_put_is_not_reported_again()
    {
        // Every cycle answers. The row must not re-fire detection, or autoflash would re-evaluate
        // the same board once per second.
        var (state, _) = ProbeRowPolicy.Advance(ProbeRowState.Initial, Answered);
        var (_, action) = ProbeRowPolicy.Advance(state, Answered);

        Assert.IsType<ProbeRowAction.None>(action);
    }

    [Fact]
    public void One_silence_does_not_retract_the_row()
    {
        // A single quiet cycle is routine — a dropped byte, a part mid-reset. Retracting here would
        // make the row flicker and, under --repeat, reopen the dedupe gate on noise.
        var occupied = Run(ProbeRowState.Initial, Answered);

        var (state, action) = ProbeRowPolicy.Advance(occupied, Silent);

        Assert.IsType<ProbeRowAction.None>(action);
        Assert.True(state.Reported);
        Assert.False(state.Occupied);
    }

    [Fact]
    public void The_row_is_retracted_after_enough_consecutive_silences()
    {
        var occupied = Run(ProbeRowState.Initial, Answered);
        var state = occupied;
        ProbeRowAction? last = null;
        for (int i = 0; i < ProbeRowPolicy.SilencesBeforeRemoved; i++)
            (state, last) = ProbeRowPolicy.Advance(state, Silent);

        Assert.IsType<ProbeRowAction.Removed>(last);
        Assert.False(state.Reported);
    }

    [Fact]
    public void The_row_is_retracted_only_once()
    {
        var state = Run(ProbeRowState.Initial, Answered, Silent, Silent, Silent);

        var (_, action) = ProbeRowPolicy.Advance(state, Silent);

        Assert.IsType<ProbeRowAction.None>(action);
    }

    [Fact]
    public void An_answer_resets_the_silence_run()
    {
        // Two quiet cycles then an answer: the board never left, so the next silence run starts
        // from zero rather than tipping the row over on its first.
        var state = Run(ProbeRowState.Initial, Answered, Silent, Silent, Answered);

        Assert.Equal(0, state.Silences);

        var (_, action) = ProbeRowPolicy.Advance(state, Silent);
        Assert.IsType<ProbeRowAction.None>(action);
    }

    [Fact]
    public void A_replacement_board_is_reported_after_the_row_was_retracted()
    {
        var retracted = Run(ProbeRowState.Initial, Answered, Silent, Silent, Silent);

        var (_, action) = ProbeRowPolicy.Advance(retracted, Answered);

        Assert.IsType<ProbeRowAction.Detected>(action);
    }

    [Fact]
    public void Silence_before_anything_was_reported_says_nothing()
    {
        // An armed fixture sitting empty is the normal resting state. It must not emit removals for
        // a board that was never there.
        var state = ProbeRowState.Initial;
        for (int i = 0; i < ProbeRowPolicy.SilencesBeforeRemoved + 2; i++)
        {
            ProbeRowAction action;
            (state, action) = ProbeRowPolicy.Advance(state, Silent);
            Assert.IsType<ProbeRowAction.None>(action);
        }
    }

    [Fact]
    public void The_row_stalls_only_after_the_backoff_threshold()
    {
        var state = Run(ProbeRowState.Initial, Enumerable.Repeat(Silent, ProbeRowPolicy.SilencesBeforeBackoff - 1).ToArray());
        Assert.False(state.Stalled);

        (state, _) = ProbeRowPolicy.Advance(state, Silent);
        Assert.True(state.Stalled);
    }

    [Fact]
    public void An_answer_clears_a_stall()
    {
        var stalled = Run(ProbeRowState.Initial, Enumerable.Repeat(Silent, ProbeRowPolicy.SilencesBeforeBackoff).ToArray());
        Assert.True(stalled.Stalled);

        var (state, _) = ProbeRowPolicy.Advance(stalled, Answered);

        Assert.False(state.Stalled);
    }

    [Fact]
    public void Backing_off_takes_much_longer_than_reporting_a_board_gone()
    {
        // Different jobs: a lifted board should disappear promptly, while backing off is about a
        // fixture that has been sitting empty.
        Assert.True(ProbeRowPolicy.SilencesBeforeBackoff > ProbeRowPolicy.SilencesBeforeRemoved);
    }

    [Fact]
    public void A_transport_failure_faults_the_row_rather_than_counting_as_silence()
    {
        // The bridge is gone or unusable. Probing harder cannot help, and calling it silence would
        // blame the board for a broken connection.
        var occupied = Run(ProbeRowState.Initial, Answered);

        var (state, action) = ProbeRowPolicy.Advance(occupied, new ProbeOutcome.TransportFailed("port closed"));

        Assert.Contains("port closed", Assert.IsType<ProbeRowAction.Faulted>(action).Message);
        Assert.Equal(ProbeRowState.Initial, state);
    }
}

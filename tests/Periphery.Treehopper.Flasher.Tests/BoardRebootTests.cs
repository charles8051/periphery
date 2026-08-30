using Periphery;

namespace Periphery.Treehopper.Flasher.Tests;

/// <summary>
/// The <c>reboot</c> verb's pure core: folding OS device notifications into a verdict is a total
/// function over values, so the transient this verb exists to catch is exercised here with no
/// hardware — including the ~224 ms drop-and-return that the old 500 ms poll reported as NO EFFECT.
/// </summary>
public class BoardRebootTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);

    private static RebootObservation Fold(params RebootSignal[] signals)
    {
        var observed = new RebootObservation();
        foreach (var signal in signals) observed = observed.Observe(signal);
        return observed;
    }

    private static RebootSignal Gone(double ms) => new(RebootSignalKind.Gone, TimeSpan.FromMilliseconds(ms));
    private static RebootSignal Back(double ms) => new(RebootSignalKind.Back, TimeSpan.FromMilliseconds(ms));

    private static DeviceInfo Board(string id, string? serial) => new() { Id = id, SerialNumber = serial };

    // ── The fold ───────────────────────────────────────────────────────────────

    [Fact]
    public void A_drop_and_return_is_a_reboot_with_the_measured_gap()
    {
        // The bench measurement from #230: removed at 22:26:08.016, activated at .240.
        var observed = Fold(Gone(16), Back(240));

        Assert.True(observed.IsComplete);
        Assert.Equal(TimeSpan.FromMilliseconds(224), observed.Gap);
        Assert.Equal(RebootOutcome.Rebooted, BoardReboot.Classify(observed));
    }

    [Fact]
    public void The_gap_and_both_edges_are_reported_in_the_verdict()
    {
        string verdict = BoardReboot.Summarize(Fold(Gone(16), Back(240)), Budget);

        Assert.StartsWith("OK", verdict);
        Assert.Contains("224 ms", verdict);        // the absence
        Assert.Contains("16 ms", verdict);         // and both edges it was measured between
        Assert.Contains("240 ms", verdict);
    }

    [Fact]
    public void A_transient_shorter_than_the_old_poll_interval_still_counts()
    {
        // The bug in one assertion: 224 ms is less than the 500 ms the verb used to sample at, and
        // it is still a reboot. Nothing in the fold is allowed to have a minimum observable gap.
        var observed = Fold(Gone(5), Back(60));

        Assert.Equal(RebootOutcome.Rebooted, BoardReboot.Classify(observed));
        Assert.Equal(TimeSpan.FromMilliseconds(55), observed.Gap);
    }

    [Fact]
    public void The_snapshots_activation_of_a_still_present_board_is_not_a_return()
    {
        // The watcher activates every already-present device when it starts, before 0x0C is sent.
        var observed = Fold(Back(0), Gone(16), Back(240));

        Assert.Equal(TimeSpan.FromMilliseconds(16), observed.DroppedAt);
        Assert.Equal(TimeSpan.FromMilliseconds(240), observed.ReturnedAt);
    }

    [Fact]
    public void Only_the_first_edge_each_way_counts()
    {
        // One physical transition arrives as deactivate + remove, and the return as appear +
        // activate. The later duplicate must not move the measured gap.
        var observed = Fold(Gone(16), Gone(18), Back(240), Back(248));

        Assert.Equal(TimeSpan.FromMilliseconds(224), observed.Gap);
    }

    [Fact]
    public void A_signal_that_adds_nothing_yields_the_same_value()
    {
        var observed = Fold(Gone(16), Back(240));

        Assert.Equal(observed, observed.Observe(Gone(900)));
        Assert.Equal(observed, observed.Observe(Back(900)));
    }

    [Fact]
    public void A_drop_with_no_return_is_a_warning_not_a_reboot()
    {
        var observed = Fold(Gone(16));

        Assert.False(observed.IsComplete);
        Assert.Null(observed.Gap);
        Assert.Equal(RebootOutcome.DroppedWithoutReturn, BoardReboot.Classify(observed));
        Assert.StartsWith("WARNING", BoardReboot.Summarize(observed, Budget));
    }

    [Fact]
    public void No_edges_at_all_is_the_only_no_effect()
    {
        var observed = Fold();

        Assert.Equal(RebootOutcome.NoDropObserved, BoardReboot.Classify(observed));

        string verdict = BoardReboot.Summarize(observed, Budget);
        Assert.StartsWith("NO EFFECT", verdict);
        // The negative is only trustworthy because the watch is event-driven; say so, and do not
        // regress to quoting a poll interval.
        Assert.Contains("notifications", verdict);
        Assert.DoesNotContain("poll", verdict);
    }

    // ── The rescue verdict (ADR-0075) ──────────────────────────────────────────

    [Fact]
    public void A_rescue_that_re_enumerated_reports_the_measured_absence()
    {
        string verdict = BoardReboot.SummarizeRescue(Fold(Gone(16), Back(240)), Budget);

        Assert.StartsWith("RESCUED", verdict);
        Assert.Contains("224 ms", verdict);        // the absence
        Assert.Contains("16 ms", verdict);         // and both edges it was measured between
        Assert.Contains("240 ms", verdict);
    }

    [Fact]
    public void A_rescue_with_no_drop_blames_missing_firmware_not_the_hardware()
    {
        // The rescue request faults whether the board reset or the firmware never implemented it,
        // so the watch is the ONLY evidence. A board that never left the bus is therefore most
        // likely running firmware without the handler — every board flashed before #227. Saying
        // "NO EFFECT" like the reboot verdict would point the operator at the wrong thing.
        string verdict = BoardReboot.SummarizeRescue(Fold(), Budget);

        Assert.StartsWith("NO RESCUE", verdict);
        Assert.Contains("firmware", verdict);
        Assert.DoesNotContain("poll", verdict);
    }

    [Fact]
    public void The_rescue_verdict_folds_the_same_way_as_the_reboot_verdict()
    {
        // Same observation type, same Classify: only the wording differs. Pinned so a future edit
        // to one verdict cannot quietly give the two different notions of success.
        var rebooted = Fold(Gone(16), Back(240));
        var droppedOnly = Fold(Gone(16));
        var nothing = Fold();

        Assert.Equal(RebootOutcome.Rebooted, BoardReboot.Classify(rebooted));
        Assert.Equal(RebootOutcome.DroppedWithoutReturn, BoardReboot.Classify(droppedOnly));
        Assert.Equal(RebootOutcome.NoDropObserved, BoardReboot.Classify(nothing));

        Assert.StartsWith("WARNING", BoardReboot.SummarizeRescue(droppedOnly, Budget));
    }

    // ── Identity across a re-enumeration ───────────────────────────────────────

    [Fact]
    public void A_board_whose_serial_comes_back_in_different_casing_is_the_same_board()
    {
        // Measured in #231: CDYHINBH re-enumerated as cDYhINBh. The ids here are a Linux sysfs path,
        // which does not embed the serial, so this pins the serial comparison itself.
        var target = Board("/sys/bus/usb/devices/1-4", "CDYHINBH");
        var returned = Board("/sys/bus/usb/devices/1-4", "cDYhINBh");

        Assert.True(BoardReboot.IsSameBoard(returned, target));
    }

    [Fact]
    public void A_removal_stub_carrying_only_an_id_is_matched_on_the_serial_inside_it()
    {
        // A removal notification can fire for a device already out of the tree, so its payload has
        // no serial to compare — only the instance id, whose last segment is the serial, and whose
        // casing has already changed by the time it comes back.
        var target = Board("USB\\VID_10C4&PID_8A7E\\CDYHINBH", "CDYHINBH");
        var stub = Board("USB\\VID_10C4&PID_8A7E\\cDYhINBh", serial: null);

        Assert.True(BoardReboot.IsSameBoard(stub, target));
    }

    [Fact]
    public void A_board_whose_serial_merely_contains_the_targets_is_not_the_same_board()
    {
        // The id fallback is a last-*segment* match, not a substring one. A substring match would
        // fold a neighbouring board's edges into this watch — the precise failure the predicate
        // exists to prevent — in three shapes: the target's serial as a prefix of another's, as a
        // suffix of another's, and buried inside one.
        var target = Board("USB\\VID_10C4&PID_8A7E\\ABCDEFG", "ABCDEFG");

        Assert.False(BoardReboot.IsSameBoard(Board("USB\\VID_10C4&PID_8A7E\\ABCDEFGH", null), target));
        Assert.False(BoardReboot.IsSameBoard(Board("USB\\VID_10C4&PID_8A7E\\XABCDEFG", null), target));
        Assert.False(BoardReboot.IsSameBoard(Board("USB\\VID_10C4&PID_8A7E\\XABCDEFGY", null), target));
    }

    [Fact]
    public void A_different_board_on_the_same_hub_is_not_the_same_board()
    {
        var target = Board("USB\\VID_10C4&PID_8A7E\\CDYHINBH", "CDYHINBH");
        var other = Board("USB\\VID_10C4&PID_8A7E\\IMNUZ6YW", "IMNUZ6YW");

        Assert.False(BoardReboot.IsSameBoard(other, target));
    }

    [Fact]
    public void A_serial_less_board_is_matched_on_its_instance_id_alone()
    {
        var target = Board("USB\\VID_10C4&PID_8A7E\\6&1F2C3D4&0&2", serial: null);

        Assert.True(BoardReboot.IsSameBoard(Board("usb\\vid_10c4&pid_8a7e\\6&1f2c3d4&0&2", null), target));
        Assert.False(BoardReboot.IsSameBoard(Board("USB\\VID_10C4&PID_8A7E\\6&1F2C3D4&0&3", null), target));
    }
}

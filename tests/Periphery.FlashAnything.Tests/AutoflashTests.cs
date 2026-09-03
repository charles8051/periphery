using System.Collections.Generic;

namespace Periphery.FlashAnything.Tests;

/// <summary>
/// The pure autoflash decision table (<see cref="AutoflashPolicy"/>) plus the reducer folds for
/// arm/disarm and the session tally. No hardware, no clock.
/// </summary>
public class AutoflashTests
{
    private const string Family = "STM32 USB DFU";
    private static readonly AutoflashConfig Armed = new(Family, FlashOptions.Default);
    private static readonly IReadOnlySet<DeviceId> NoneFlashed = new HashSet<DeviceId>();

    private static FlashTargetView Target(
        string id, string provider = Family, IdentificationMode mode = IdentificationMode.Passive)
        => new(id, id, provider, mode);

    // ── AutoflashPolicy.Decide ──────────────────────────────────────────

    [Fact]
    public void Flashes_an_armed_passive_unflashed_target()
        => Assert.IsType<AutoflashAction.Flash>(AutoflashPolicy.Decide(Armed, Target("dfu"), NoneFlashed));

    [Fact]
    public void Skips_a_target_of_a_different_family()
    {
        var action = AutoflashPolicy.Decide(Armed, Target("efm8", provider: "EFM8 USB"), NoneFlashed);
        Assert.Contains("not the armed family", Assert.IsType<AutoflashAction.Skip>(action).Reason);
    }

    [Fact]
    public void Skips_a_probe_identified_target()
    {
        // Rule 2 is now a scope check rather than a flat ban (adr.md Decision 8), but an arm that
        // bound no bridge authorises no probing — which is what this has always asserted.
        var action = AutoflashPolicy.Decide(Armed, Target("port", mode: IdentificationMode.Probe), NoneFlashed);
        Assert.Contains("probe-identified", Assert.IsType<AutoflashAction.Skip>(action).Reason);
    }

    [Fact]
    public void Skips_a_target_already_flashed_this_session()
    {
        var action = AutoflashPolicy.Decide(Armed, Target("dfu"), new HashSet<DeviceId> { "dfu" });
        Assert.Contains("already flashed", Assert.IsType<AutoflashAction.Skip>(action).Reason);
    }

    [Fact]
    public void Family_gate_takes_precedence_over_passive_and_dedupe()
    {
        // A wrong-family + probe + already-flashed target reports the family skip (checked first).
        var action = AutoflashPolicy.Decide(
            Armed, Target("x", provider: "Other", mode: IdentificationMode.Probe), new HashSet<DeviceId> { "x" });
        Assert.Contains("not the armed family", Assert.IsType<AutoflashAction.Skip>(action).Reason);
    }

    // ── Reducer: arm / disarm / tally ───────────────────────────────────

    [Fact]
    public void Arming_sets_the_config_and_resets_the_tally()
    {
        var armed = AppReducer.ReduceAll(
            AppState.Empty,
            new AppEvent.AutoflashOutcome("stale", AutoflashOutcomeKind.Flashed), // pre-existing tally
            new AppEvent.AutoflashArmed(Armed));

        Assert.Equal(Armed, armed.Autoflash);
        Assert.Equal(0, armed.AutoflashTally.Total); // reset on arm
    }

    [Fact]
    public void Disarming_clears_the_config_but_keeps_the_tally()
    {
        var state = AppReducer.ReduceAll(
            AppState.Empty,
            new AppEvent.AutoflashArmed(Armed),
            new AppEvent.AutoflashOutcome("a", AutoflashOutcomeKind.Flashed),
            new AppEvent.AutoflashDisarmed());

        Assert.Null(state.Autoflash);
        Assert.Equal(1, state.AutoflashTally.Flashed); // the audit/tally survive disarm
    }

    [Fact]
    public void Outcomes_fold_into_counts_and_audit()
    {
        var state = AppReducer.ReduceAll(
            AppState.Empty,
            new AppEvent.AutoflashArmed(Armed),
            new AppEvent.AutoflashOutcome("a", AutoflashOutcomeKind.Flashed),
            new AppEvent.AutoflashOutcome("b", AutoflashOutcomeKind.Failed, "wedged"),
            new AppEvent.AutoflashOutcome("c", AutoflashOutcomeKind.Skipped, "already flashed this session"));

        var tally = state.AutoflashTally;
        Assert.Equal(1, tally.Flashed);
        Assert.Equal(1, tally.Failed);
        Assert.Equal(1, tally.Skipped);
        Assert.Equal(3, tally.Total);
        Assert.Equal(3, tally.Audit.Length);
        Assert.Contains("failed b: wedged", tally.Audit);
        Assert.Contains("skipped c: already flashed this session", tally.Audit);
    }
}

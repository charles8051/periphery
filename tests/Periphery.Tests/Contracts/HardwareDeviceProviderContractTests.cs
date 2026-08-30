namespace Periphery.Tests;

/// <summary>
/// <see cref="DeviceProviderContractTests"/> specialised for providers backed by
/// real hardware, where <c>seeds</c> is ignored and the dataset is whatever the
/// host machine happens to be reporting.
/// </summary>
/// <remarks>
/// <para>
/// Two groups of inherited assertions do not hold as unit tests against live
/// hardware, for two different reasons:
/// </para>
/// <para>
/// <b>1. Seed-count assertions — skipped.</b> They assert
/// <c>results.Count == AllTestDevices().Length</c> (5), which is never true of a
/// real machine. They are superseded by the invariant assertions, which verify
/// correctness without relying on a fixed device count.
/// </para>
/// <para>
/// <b>2. Cross-enumeration comparisons — re-tiered to <c>Category=Integration</c>.</b>
/// These call the provider twice — once unfiltered, once with a category push-down —
/// and compare the two result sets. That comparison silently assumes the dataset is
/// identical across both calls. A seeded provider guarantees that; a hardware
/// provider cannot. Any device churn in the window between the two enumerations
/// (a USB hub re-enumerating, a composite device settling, an idle-power
/// transition) makes a device appear in one set and not the other, and the
/// assertion fails on a machine whose provider is perfectly correct. The wider the
/// gap between the calls the likelier it is, so the failure correlates with CPU
/// load rather than with anything about the code under test (see issue #159:
/// 2 failures in 10 contended runs, 0 in 6 idle runs).
/// </para>
/// <para>
/// They are <em>not</em> weakened and <em>not</em> retried — a retry would paper
/// over a genuine push-down bug, which is the one thing these assertions exist to
/// catch. They keep their exact assertions and move to the <c>Integration</c> tier,
/// where live-hardware variance is expected and a re-run is a human decision.
/// </para>
/// <para>
/// The push-down property itself stays under the PR/release gate: it is asserted
/// against the seeded dataset by <see cref="FakeProviderContractTests"/>, on every
/// platform, in the <c>Category!=Integration</c> tier. What the hardware subclasses
/// add on top of that — and what moves here — is validation of the real OS-level
/// push-down (SetupAPI class GUIDs / libudev subsystems / IOKit classes), which can
/// only be exercised against real hardware and therefore can only be raced by it.
/// </para>
/// </remarks>
public abstract class HardwareDeviceProviderContractTests : DeviceProviderContractTests
{
    // ── Seed-count tests — not applicable to hardware providers ───────

    [Fact(Skip = "Hardware provider returns real device count, not seed count.")]
    public override async Task EmptyFilter_ReturnsAllSeededDevices()
        => await base.EmptyFilter_ReturnsAllSeededDevices();

    [Fact(Skip = "Hardware provider always returns present devices; empty result is not expected.")]
    public override async Task EmptyDataset_EmptyFilter_ReturnsEmpty()
        => await base.EmptyDataset_EmptyFilter_ReturnsEmpty();

    [Fact(Skip = "Hardware provider returns real device count, not seed count.")]
    public override async Task ConcurrentEnumerations_TwoCalls_BothReturnCompleteResults()
        => await base.ConcurrentEnumerations_TwoCalls_BothReturnCompleteResults();

    [Fact(Skip = "Hardware provider returns real device count, not seed count.")]
    public override async Task ConcurrentEnumerations_FiveParallel_AllReturnCompleteResults()
        => await base.ConcurrentEnumerations_FiveParallel_AllReturnCompleteResults();

    // ── Cross-enumeration comparisons — Integration tier ──────────────
    // Assertions unchanged; only the tier moves. See the remarks above.
    //
    // These overrides carry ONLY [Trait]. xUnit resolves [Theory] / [Fact] and
    // [InlineData] through the inherited attributes on the base method, so
    // re-declaring the data attributes here would discover every theory case
    // twice.

    [Trait("Category", "Integration")]
    public override async Task CategoryFilter_PushDownNeverOmitsMatchingDevices(DeviceCategory category)
        => await base.CategoryFilter_PushDownNeverOmitsMatchingDevices(category);

    [Trait("Category", "Integration")]
    public override async Task CategoryFilter_InMemoryFilteredResult_MatchesGroundTruth(DeviceCategory category)
        => await base.CategoryFilter_InMemoryFilteredResult_MatchesGroundTruth(category);

    [Trait("Category", "Integration")]
    public override async Task AllCategory_BehavesLikeEmptyFilter()
        => await base.AllCategory_BehavesLikeEmptyFilter();
}

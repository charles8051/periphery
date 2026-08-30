namespace Periphery.Tests;

/// <summary>
/// Abstract contract test suite for <see cref="IDeviceProvider"/>.
/// Subclass and implement <see cref="Enumerate"/> to verify a concrete provider.
/// </summary>
/// <remarks>
/// Contract rules under test:
/// <list type="number">
///   <item>Every returned <see cref="DeviceInfo.Id"/> is non-null and non-whitespace.</item>
///   <item><see cref="DeviceInfo.Category"/> is always a defined <see cref="DeviceCategory"/> value.</item>
///   <item>An empty <see cref="DeviceFilter"/> returns all seeded devices (no spurious filtering).</item>
///   <item>Category push-down never omits a device that passes <see cref="DeviceFilter.Matches"/>;
///       correctness never depends on provider cooperation with the filter hint.</item>
///   <item>Push-down invariant: after in-memory <c>Matches()</c>, the filtered and unfiltered
///       outputs are equivalent — <c>provider(filter).Where(Matches) == provider(∅).Where(Matches)</c>.</item>
///   <item>A pre-cancelled <see cref="CancellationToken"/> stops enumeration.</item>
///   <item>Concurrent enumerations are isolated — no shared state corruption.</item>
/// </list>
/// <para>
/// To test a new provider (Linux, macOS, etc.) simply subclass this and implement
/// <see cref="Enumerate"/>. All 14 contract assertions are inherited automatically.
/// </para>
/// <para>
/// <b>Dataset stability.</b> Some assertions call <see cref="Enumerate"/> twice and
/// compare the two results (rules 3–5 above). They are only meaningful when the
/// provider's dataset is stable between the two calls — true for a seeded provider,
/// false for a hardware-backed one, where a hub re-enumerating or a device settling
/// between the calls changes the answer. Those methods are <c>virtual</c> so a
/// hardware subclass can re-tier or skip them; see
/// <see cref="HardwareDeviceProviderContractTests"/>, which does exactly that.
/// </para>
/// </remarks>
public abstract class DeviceProviderContractTests
{
    /// <summary>
    /// Enumerate devices from the provider under test, seeded with <paramref name="seeds"/>.
    /// The provider must honour <paramref name="filter"/> as a push-down hint but is free
    /// to return additional devices — the contract tests re-apply <c>Matches()</c> to verify.
    /// </summary>
    protected abstract IAsyncEnumerable<DeviceInfo> Enumerate(
        DeviceInfo[] seeds, DeviceFilter filter, CancellationToken ct = default);

    // ── Canonical test dataset ─────────────────────────────────────────
    // A stable mix: 3 USB (one disconnected), 1 Network, 1 HID.
    // All categories are represented for theory-driven push-down tests.

    private static DeviceInfo D(string id, DeviceCategory cat, bool connected = true) => new()
    {
        Id = id,
        Name = id,
        Category = cat,
        IsActive = connected,
        Status = DeviceStatus.OK,
    };

    private static DeviceInfo[] AllTestDevices() =>
    [
        D("USB\\1", DeviceCategory.Usb),
        D("USB\\2", DeviceCategory.Usb),
        D("USB\\3", DeviceCategory.Usb, connected: false),
        D("NET\\1", DeviceCategory.Network),
        D("HID\\1", DeviceCategory.Hid),
    ];

    // ── Empty filter ───────────────────────────────────────────────────

    [Fact]
    public virtual async Task EmptyFilter_ReturnsAllSeededDevices()
    {
        var devices = AllTestDevices();
        var results = await CollectAsync(devices, new DeviceFilter());

        Assert.Equal(devices.Length, results.Count);
    }

    [Fact]
    public virtual async Task EmptyDataset_EmptyFilter_ReturnsEmpty()
    {
        var results = await CollectAsync([], new DeviceFilter());

        Assert.Empty(results);
    }

    // ── DeviceInfo invariants ──────────────────────────────────────────

    [Fact]
    public async Task AllReturnedDevices_IdIsNotNullOrWhiteSpace()
    {
        var results = await CollectAsync(AllTestDevices(), new DeviceFilter());

        Assert.All(results, d => Assert.False(
            string.IsNullOrWhiteSpace(d.Id),
            $"Device Id must not be null or whitespace; got: '{d.Id}'"));
    }

    [Fact]
    public async Task AllReturnedDevices_CategoryIsDefinedEnumValue()
    {
        var results = await CollectAsync(AllTestDevices(), new DeviceFilter());

        Assert.All(results, d => Assert.True(
            Enum.IsDefined(d.Category),
            $"Device '{d.Id}' has undefined Category value {(int)d.Category}"));
    }

    // ── Push-down invariant ────────────────────────────────────────────
    // Rule: provider(filter).Where(Matches) == provider(∅).Where(Matches)
    //
    // The provider MAY push the category hint to the OS as a performance
    // optimisation, but MUST NOT omit any device that would match in-memory.
    // DeviceQuery always re-applies Matches() regardless.

    [Theory]
    [InlineData(DeviceCategory.Usb)]
    [InlineData(DeviceCategory.Network)]
    [InlineData(DeviceCategory.Hid)]
    public virtual async Task CategoryFilter_PushDownNeverOmitsMatchingDevices(DeviceCategory category)
    {
        var filter = new DeviceFilter();
        filter.OfCategory(category);

        // Ground truth: what a correct in-memory filter produces from the full set.
        var unfiltered = await CollectAsync(AllTestDevices(), new DeviceFilter());
        var filtered   = await CollectAsync(AllTestDevices(), filter);

        var expectedIds = unfiltered.Where(d => filter.Matches(d)).Select(d => d.Id).ToHashSet();
        var actualIds   = filtered.Select(d => d.Id).ToHashSet();

        Assert.True(
            expectedIds.IsSubsetOf(actualIds),
            $"Push-down for {category} omitted: [{string.Join(", ", expectedIds.Except(actualIds))}]");
    }

    [Theory]
    [InlineData(DeviceCategory.Usb)]
    [InlineData(DeviceCategory.Network)]
    [InlineData(DeviceCategory.Hid)]
    public virtual async Task CategoryFilter_InMemoryFilteredResult_MatchesGroundTruth(DeviceCategory category)
    {
        // Full equivalence test:
        //   provider(filter).Where(Matches) == provider(∅).Where(Matches)
        var filter = new DeviceFilter();
        filter.OfCategory(category);

        var unfiltered = await CollectAsync(AllTestDevices(), new DeviceFilter());
        var filtered   = await CollectAsync(AllTestDevices(), filter);

        var groundTruth = unfiltered.Where(d => filter.Matches(d)).Select(d => d.Id).ToHashSet();
        var actual      = filtered  .Where(d => filter.Matches(d)).Select(d => d.Id).ToHashSet();

        Assert.Equal(groundTruth, actual);
    }

    [Fact]
    public virtual async Task AllCategory_BehavesLikeEmptyFilter()
    {
        var allFilter = new DeviceFilter();
        allFilter.OfCategory(DeviceCategory.All);

        var unfiltered = await CollectAsync(AllTestDevices(), new DeviceFilter());
        var withAll    = await CollectAsync(AllTestDevices(), allFilter);

        var groundTruth = unfiltered.Where(d => allFilter.Matches(d)).Select(d => d.Id).ToHashSet();
        var actual      = withAll   .Where(d => allFilter.Matches(d)).Select(d => d.Id).ToHashSet();

        Assert.Equal(groundTruth, actual);
    }

    [Fact]
    public async Task CategoryFilter_NoMatchingDevices_InMemoryResultIsEmpty()
    {
        // A capability tag no in-memory test fixture carries — exercises the
        // empty-result path without depending on host hardware. (Was
        // OfCategory(SmartCard) before ADR-0051 demoted SmartCard to a tag.)
        var filter = new DeviceFilter();
        filter.WithTag(DeviceTags.SmartCard);

        var filtered = await CollectAsync(AllTestDevices(), filter);
        var matched  = filtered.Where(d => filter.Matches(d)).ToList();

        Assert.Empty(matched);
    }

    // ── Cancellation ───────────────────────────────────────────────────

    [Fact]
    public async Task EnumerateAsync_PreCancelledToken_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in Enumerate(AllTestDevices(), new DeviceFilter(), cts.Token))
            {
                // must not reach here
            }
        });
    }

    // ── Concurrent enumeration ─────────────────────────────────────────

    [Fact]
    public virtual async Task ConcurrentEnumerations_TwoCalls_BothReturnCompleteResults()
    {
        var expected = AllTestDevices().Length;

        var t1 = CollectAsync(AllTestDevices(), new DeviceFilter());
        var t2 = CollectAsync(AllTestDevices(), new DeviceFilter());
        await Task.WhenAll(t1, t2);

        Assert.Equal(expected, t1.Result.Count);
        Assert.Equal(expected, t2.Result.Count);
    }

    [Fact]
    public virtual async Task ConcurrentEnumerations_FiveParallel_AllReturnCompleteResults()
    {
        var expected = AllTestDevices().Length;

        var tasks = Enumerable.Range(0, 5)
            .Select(_ => CollectAsync(AllTestDevices(), new DeviceFilter()))
            .ToArray();

        var allResults = await Task.WhenAll(tasks);

        Assert.All(allResults, r => Assert.Equal(expected, r.Count));
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private async Task<List<DeviceInfo>> CollectAsync(
        DeviceInfo[] seeds, DeviceFilter filter, CancellationToken ct = default)
    {
        var result = new List<DeviceInfo>();
        await foreach (var d in Enumerate(seeds, filter, ct))
            result.Add(d);
        return result;
    }
}

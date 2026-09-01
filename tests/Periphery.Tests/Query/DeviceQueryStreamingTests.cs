namespace Periphery.Tests;

/// <summary>
/// <see cref="DeviceQuery.OrderBy{TKey}"/> is the only operator that buffers.
/// </summary>
/// <remarks>
/// <para>
/// Every assertion here is on <see cref="FakeDeviceProvider.Yielded"/> — how
/// many devices the provider was actually asked to produce — not on the query's
/// results. Asserting on results alone would pass just as happily against the
/// old unconditional buffer, which walked every device on the box and then threw
/// away all but the first.
/// </para>
/// <para>
/// That walk is not free: on Windows each device costs a cfgmgr32 property read,
/// and the filter itself opts into monitor and battery enrichment.
/// </para>
/// </remarks>
public class DeviceQueryStreamingTests
{
    private static DeviceInfo Device(string id, string name) =>
        new()
        {
            Id = id,
            Name = name,
            Category = DeviceCategory.Usb,
            IsActive = true,
        };

    private static FakeDeviceProvider FiveDevices() =>
        new(
            Device("USB\\1", "Alpha"),
            Device("USB\\2", "Bravo"),
            Device("USB\\3", "Charlie"),
            Device("USB\\4", "Delta"),
            Device("USB\\5", "Echo")
        );

    // ── The point of the change ────────────────────────────────────────

    [Fact]
    public async Task FirstOrDefaultAsync_StopsAtTheFirstMatch()
    {
        var provider = FiveDevices();

        var first = await new DeviceQuery(provider).FirstOrDefaultAsync();

        Assert.Equal("Alpha", first!.Name);
        Assert.Equal(1, provider.Yielded);
        Assert.False(provider.EnumeratedToCompletion);
    }

    [Fact]
    public async Task AnyAsync_StopsAtTheFirstMatch()
    {
        var provider = FiveDevices();

        Assert.True(await new DeviceQuery(provider).AnyAsync());

        Assert.Equal(1, provider.Yielded);
        Assert.False(provider.EnumeratedToCompletion);
    }

    [Fact]
    public async Task TakeAlone_StopsAfterTheNthMatch()
    {
        var provider = FiveDevices();

        var results = await new DeviceQuery(provider).Take(2).ToListAsync();

        Assert.Equal(["Alpha", "Bravo"], results.Select(d => d.Name));
        Assert.Equal(2, provider.Yielded);
        Assert.False(provider.EnumeratedToCompletion);
    }

    [Fact]
    public async Task ACallerThatBreaksEarly_StopsTheProvider()
    {
        var provider = FiveDevices();

        await foreach (var _ in new DeviceQuery(provider))
            break;

        Assert.Equal(1, provider.Yielded);
        Assert.False(provider.EnumeratedToCompletion);
    }

    [Fact]
    public async Task AFilteredQuery_StopsAtItsFirstMatch_NotTheFirstDevice()
    {
        var provider = FiveDevices();

        var match = await new DeviceQuery(provider).WithName("Charlie").FirstOrDefaultAsync();

        Assert.Equal("Charlie", match!.Name);

        // Three produced, not five: the walk stops at the match rather than
        // running to the end and filtering afterwards.
        Assert.Equal(3, provider.Yielded);
        Assert.False(provider.EnumeratedToCompletion);
    }

    // ── OrderBy still buffers, because it must ─────────────────────────

    [Fact]
    public async Task OrderBy_StillEnumeratesEverything()
    {
        var provider = FiveDevices();

        var results = await new DeviceQuery(provider).OrderBy(d => d.Name!).ToListAsync();

        Assert.Equal(["Alpha", "Bravo", "Charlie", "Delta", "Echo"], results.Select(d => d.Name));

        // A sort cannot name its first result without seeing every candidate.
        Assert.Equal(5, provider.Yielded);
        Assert.True(provider.EnumeratedToCompletion);
    }

    [Fact]
    public async Task OrderByWithTake_StillEnumeratesEverything_AndSortsBeforeLimiting()
    {
        var provider = FiveDevices();

        var results = await new DeviceQuery(provider)
            .OrderBy(d => d.Name!, descending: true)
            .Take(2)
            .ToListAsync();

        // Echo and Delta, not the first two in provider order — which is the
        // whole reason this path cannot stop early.
        Assert.Equal(["Echo", "Delta"], results.Select(d => d.Name));
        Assert.Equal(5, provider.Yielded);
        Assert.True(provider.EnumeratedToCompletion);
    }

    [Fact]
    public async Task OrderByWithFirstOrDefault_ReturnsTheSortedFirst_NotTheProviderFirst()
    {
        var provider = FiveDevices();

        var first = await new DeviceQuery(provider)
            .OrderBy(d => d.Name!, descending: true)
            .FirstOrDefaultAsync();

        Assert.Equal("Echo", first!.Name);
        Assert.Equal(5, provider.Yielded);
    }

    // ── Unchanged results ──────────────────────────────────────────────

    [Fact]
    public async Task ToListAsync_IsUnchanged()
    {
        var provider = FiveDevices();

        var results = await new DeviceQuery(provider).ToListAsync();

        Assert.Equal(5, results.Count);
        Assert.Equal(["Alpha", "Bravo", "Charlie", "Delta", "Echo"], results.Select(d => d.Name));
        Assert.True(provider.EnumeratedToCompletion);
    }

    [Fact]
    public async Task CountAsync_IsUnchanged()
    {
        Assert.Equal(5, await new DeviceQuery(FiveDevices()).CountAsync());
        Assert.Equal(2, await new DeviceQuery(FiveDevices()).Take(2).CountAsync());
    }

    [Fact]
    public async Task StreamedOrder_IsProviderOrder()
    {
        var results = await new DeviceQuery(FiveDevices()).ToListAsync();
        Assert.Equal(["Alpha", "Bravo", "Charlie", "Delta", "Echo"], results.Select(d => d.Name));
    }

    [Fact]
    public async Task AQueryMatchingNothing_ReturnsNull_AndStillWalksEverything()
    {
        var provider = FiveDevices();

        Assert.Null(await new DeviceQuery(provider).WithName("Zulu").FirstOrDefaultAsync());

        // No early exit is possible when there is nothing to exit on.
        Assert.True(provider.EnumeratedToCompletion);
    }

    [Fact]
    public async Task TakeMoreThanAvailable_YieldsWhatThereIs()
    {
        var provider = FiveDevices();

        var results = await new DeviceQuery(provider).Take(50).ToListAsync();

        Assert.Equal(5, results.Count);
        Assert.True(provider.EnumeratedToCompletion);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void TakeRejectsNonPositiveCounts(int count)
    {
        // Which is why the streaming path can check its limit *after* yielding:
        // _limit is never below 1, so the check can stop the walk on the nth
        // match rather than having to see an n+1th to know it is done.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DeviceQuery(FiveDevices()).Take(count)
        );
    }
}

using System.Collections.Immutable;

namespace Periphery.Tests;

/// <summary>
/// The tag filters take <c>params string[]</c>. Their match predicate outlives
/// the call, so it must read a snapshot rather than the caller's array — a
/// captured array lets a caller rewrite a filter's criteria after the fact,
/// including on a <see cref="DeviceWatcher"/> that has already started and whose
/// configure-time guard has long since passed.
/// </summary>
public class DeviceFilterTagSnapshotTests
{
    private static DeviceInfo Tagged(params string[] tags) =>
        new()
        {
            Id = "test",
            Category = DeviceCategory.Usb,
            Tags = [.. tags],
        };

    [Fact]
    public void WithAllTags_IgnoresLaterMutationOfTheCallerArray()
    {
        var tags = new[] { "Printer" };
        var filter = new DeviceFilter().WithAllTags(tags);

        tags[0] = "Battery";

        Assert.True(filter.Matches(Tagged("Printer")));
        Assert.False(filter.Matches(Tagged("Battery")));
    }

    [Fact]
    public void WithAnyTag_IgnoresLaterMutationOfTheCallerArray()
    {
        var tags = new[] { "Printer", "Imaging" };
        var filter = new DeviceFilter().WithAnyTag(tags);

        tags[0] = "Battery";
        tags[1] = "Battery";

        Assert.True(filter.Matches(Tagged("Imaging")));
        Assert.False(filter.Matches(Tagged("Battery")));
    }

    [Fact]
    public void WithAllTags_StillMatchesEveryListedTag()
    {
        var filter = new DeviceFilter().WithAllTags("Printer", "Imaging");

        Assert.True(filter.Matches(Tagged("Printer", "Imaging")));
        Assert.False(filter.Matches(Tagged("Printer")));
    }
}

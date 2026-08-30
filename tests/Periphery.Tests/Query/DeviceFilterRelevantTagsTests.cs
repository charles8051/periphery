namespace Periphery.Tests;

/// <summary>
/// Pins <see cref="DeviceFilter.RelevantTags"/> (ADR-0051 §5) — the structured
/// capture of the tags a filter references, which lets a provider scope a bare
/// tag query. The captured set is a hint only; <see cref="DeviceFilter.Matches"/>
/// still does the actual filtering via the tag predicate.
/// </summary>
public class DeviceFilterRelevantTagsTests
{
    [Fact]
    public void NoTagFilter_RelevantTagsEmpty()
    {
        var filter = new DeviceFilter().OfCategory(DeviceCategory.Ports);
        Assert.Empty(filter.RelevantTags);
    }

    [Fact]
    public void WithTag_CapturesTheTag()
    {
        var filter = new DeviceFilter().WithTag("Gps");
        Assert.Equal(["Gps"], filter.RelevantTags.Order());
    }

    [Fact]
    public void WithAllTags_CapturesEveryTag()
    {
        var filter = new DeviceFilter().WithAllTags("Gps", "Serial");
        Assert.Equal(["Gps", "Serial"], filter.RelevantTags.Order());
    }

    [Fact]
    public void WithAnyTag_CapturesEveryTag()
    {
        var filter = new DeviceFilter().WithAnyTag("Printer", "Imaging");
        Assert.Equal(["Imaging", "Printer"], filter.RelevantTags.Order());
    }

    [Fact]
    public void RelevantTags_DeDuplicatesAcrossCalls()
    {
        var filter = new DeviceFilter().WithTag("Gps").WithAllTags("Gps", "Serial");
        Assert.Equal(["Gps", "Serial"], filter.RelevantTags.Order());
    }

    [Fact]
    public void EmptyWithAllTags_AddsNoRelevantTags()
    {
        // WithAllTags() with no args is a no-op match-everything filter; it
        // must not register any relevant tag.
        var filter = new DeviceFilter().WithAllTags();
        Assert.Empty(filter.RelevantTags);
    }

    [Fact]
    public void RelevantTags_IsHintOnly_MatchesStillFiltersByPredicate()
    {
        var filter = new DeviceFilter().WithTag("Gps");

        var tagged = new DeviceInfo { Id = "a", Category = DeviceCategory.Ports, Tags = ["Gps"] };
        var untagged = new DeviceInfo { Id = "b", Category = DeviceCategory.Ports };

        Assert.True(filter.Matches(tagged));
        Assert.False(filter.Matches(untagged));
    }

    [Fact]
    public void CopyTo_CopiesRelevantTags()
    {
        var source = new DeviceFilter().WithTag("Gps");
        var target = new DeviceFilter();

        source.CopyTo(target);

        Assert.Contains("Gps", target.RelevantTags);
    }
}

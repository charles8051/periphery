using System.Collections.Generic;
using System.Text.Json;

namespace Periphery.Tests;

public class DeviceIdTests
{
    private const string Sample = @"USB\VID_046D&PID_C52B\6&1a2b3c4d&0&2";

    // ── Construction ───────────────────────────────────────────────────

    [Fact]
    public void Constructor_WithValidValue_StoresValue()
    {
        var id = new DeviceId(Sample);
        Assert.Equal(Sample, id.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_WithEmptyOrWhitespace_Throws(string? value)
    {
        Assert.Throws<ArgumentException>(() => new DeviceId(value!));
    }

    [Fact]
    public void Constructor_WithNull_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new DeviceId(null!));
    }

    // ── Parse / TryParse ───────────────────────────────────────────────

    [Fact]
    public void Parse_ValidValue_ReturnsInstance()
    {
        var id = DeviceId.Parse(Sample);
        Assert.Equal(Sample, id.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_InvalidValue_ThrowsFormatException(string? value)
    {
        Assert.Throws<FormatException>(() => DeviceId.Parse(value!));
    }

    [Fact]
    public void TryParse_ValidValue_ReturnsTrueAndResult()
    {
        Assert.True(DeviceId.TryParse(Sample, out var result));
        Assert.Equal(Sample, result.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParse_InvalidValue_ReturnsFalse(string? value)
    {
        Assert.False(DeviceId.TryParse(value, out _));
    }

    // ── Case-insensitive equality (the load-bearing invariant) ─────────

    [Fact]
    public void Equals_SameCasing_ReturnsTrue()
    {
        Assert.Equal(new DeviceId(Sample), new DeviceId(Sample));
    }

    [Fact]
    public void Equals_DifferentCasing_ReturnsTrue()
    {
        // Instance ids are case-insensitive by contract: the same physical
        // device can re-enumerate with different casing (firmware reboot, or
        // the snapshot vs. notification path reporting different case).
        var lower = new DeviceId(Sample.ToLowerInvariant());
        var upper = new DeviceId(Sample.ToUpperInvariant());
        Assert.Equal(lower, upper);
        Assert.True(lower == upper);
        Assert.False(lower != upper);
    }

    [Fact]
    public void Equals_DifferentValue_ReturnsFalse()
    {
        Assert.NotEqual(new DeviceId(@"USB\A"), new DeviceId(@"USB\B"));
    }

    [Fact]
    public void GetHashCode_DiffersOnlyByCasing_SameHash()
    {
        var lower = new DeviceId(Sample.ToLowerInvariant());
        var upper = new DeviceId(Sample.ToUpperInvariant());
        Assert.Equal(lower.GetHashCode(), upper.GetHashCode());
    }

    [Fact]
    public void Dictionary_KeyedByDeviceId_IsCaseInsensitive()
    {
        // The whole point of moving the invariant into the type: a plain
        // Dictionary<DeviceId, _> keys case-insensitively with no comparer,
        // so a casing flip does not produce a phantom duplicate.
        var map = new Dictionary<DeviceId, int>
        {
            [new DeviceId(Sample.ToLowerInvariant())] = 1,
        };

        Assert.True(map.ContainsKey(new DeviceId(Sample.ToUpperInvariant())));
        map[new DeviceId(Sample.ToUpperInvariant())] = 2;
        Assert.Single(map);
        Assert.Equal(2, map[new DeviceId(Sample)]);
    }

    // ── Conversions / ToString ─────────────────────────────────────────

    [Fact]
    public void ImplicitConversion_FromString_Wraps()
    {
        DeviceId id = Sample;
        Assert.Equal(Sample, id.Value);
    }

    [Fact]
    public void ImplicitConversion_ToString_Unwraps()
    {
        string s = new DeviceId(Sample);
        Assert.Equal(Sample, s);
    }

    [Fact]
    public void ToString_ReturnsValue()
    {
        Assert.Equal(Sample, new DeviceId(Sample).ToString());
    }

    // ── JSON: bare string, round-trips ─────────────────────────────────

    [Fact]
    public void Json_SerializesAsBareString()
    {
        // The wire representation must be a plain JSON string, not an object,
        // so existing consumers and persisted payloads are unchanged.
        string json = JsonSerializer.Serialize(new DeviceId(Sample), DeviceInfoJsonContext.Default.DeviceId);
        Assert.Equal(JsonSerializer.Serialize(Sample), json);
    }

    [Fact]
    public void Json_RoundTrips()
    {
        var original = new DeviceId(Sample);
        string json = JsonSerializer.Serialize(original, DeviceInfoJsonContext.Default.DeviceId);
        var back = JsonSerializer.Deserialize(json, DeviceInfoJsonContext.Default.DeviceId);
        Assert.Equal(original, back);
    }

    [Fact]
    public void Json_DeviceInfo_IdAndParentId_AreBareStrings()
    {
        var device = new DeviceInfo
        {
            Id = @"USB\ROOT_HUB30\4&CHILD",
            ParentId = @"USB\ROOT_HUB30\4&PARENT",
        };

        string json = JsonSerializer.Serialize(device, DeviceInfoJsonContext.Default.DeviceInfo);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.String, doc.RootElement.GetProperty("id").ValueKind);
        Assert.Equal(@"USB\ROOT_HUB30\4&CHILD", doc.RootElement.GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.String, doc.RootElement.GetProperty("parentId").ValueKind);
        Assert.Equal(@"USB\ROOT_HUB30\4&PARENT", doc.RootElement.GetProperty("parentId").GetString());
    }
}

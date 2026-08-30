using System.Drawing;
using System.Text.Json;

namespace Periphery.Tests;

public class DrawingJsonConverterTests
{
    // For testing converters standalone — does NOT use the source-gen context
    // so the Converters collection is the only registration in play.
    private static readonly JsonSerializerOptions s_converterOpts = new()
    {
        Converters =
        {
            new SizeJsonConverter(),
            new SizeFJsonConverter(),
            new RectangleJsonConverter(),
        },
    };

    // ── SizeJsonConverter ──────────────────────────────────────────────

    [Theory]
    [InlineData(1920, 1080, "\"1920x1080\"")]
    [InlineData(2560, 1440, "\"2560x1440\"")]
    [InlineData(0, 0, "\"0x0\"")]
    public void Size_Serialize_ProducesCompactString(int w, int h, string expected)
    {
        var json = JsonSerializer.Serialize(new Size(w, h), s_converterOpts);
        Assert.Equal(expected, json);
    }

    [Theory]
    [InlineData("\"1920x1080\"", 1920, 1080)]
    [InlineData("\"2560x1440\"", 2560, 1440)]
    public void Size_Deserialize_RoundTrips(string json, int expectedW, int expectedH)
    {
        var size = JsonSerializer.Deserialize<Size>(json, s_converterOpts);
        Assert.Equal(new Size(expectedW, expectedH), size);
    }

    [Fact]
    public void Size_NullableNull_SerializesAsNull()
    {
        var device = new DeviceInfo { Id = "test" };
        var json = JsonSerializer.Serialize(device, DeviceInfoJsonContext.Default.DeviceInfo);
        Assert.DoesNotContain("displayResolution", json);
    }

    [Fact]
    public void Size_NullableValue_SerializesAsString()
    {
        var device = new DeviceInfo { Id = "test", DisplayResolution = new Size(1920, 1080) };
        var json = JsonSerializer.Serialize(device, DeviceInfoJsonContext.Default.DeviceInfo);
        Assert.Contains("\"displayResolution\":\"1920x1080\"", json);
    }

    // ── SizeFJsonConverter ─────────────────────────────────────────────

    [Theory]
    [InlineData(93.6f, 93.6f, "\"93.6x93.6\"")]
    [InlineData(96f, 96f, "\"96x96\"")]
    public void SizeF_Serialize_ProducesCompactString(float w, float h, string expected)
    {
        var json = JsonSerializer.Serialize(new SizeF(w, h), s_converterOpts);
        Assert.Equal(expected, json);
    }

    [Theory]
    [InlineData("\"93.6x93.6\"", 93.6f, 93.6f)]
    [InlineData("\"96x96\"", 96f, 96f)]
    public void SizeF_Deserialize_RoundTrips(string json, float expectedW, float expectedH)
    {
        var size = JsonSerializer.Deserialize<SizeF>(json, s_converterOpts);
        Assert.Equal(expectedW, size.Width, precision: 2);
        Assert.Equal(expectedH, size.Height, precision: 2);
    }

    [Fact]
    public void SizeF_NullableValue_SerializesAsString()
    {
        var device = new DeviceInfo { Id = "test", DisplayDpi = new SizeF(93.6f, 93.6f) };
        var json = JsonSerializer.Serialize(device, DeviceInfoJsonContext.Default.DeviceInfo);
        Assert.Contains("\"displayDpi\":\"93.6x93.6\"", json);
    }

    // ── RectangleJsonConverter ─────────────────────────────────────────

    [Theory]
    [InlineData(0, 0, 1920, 1080, "\"0,0 1920x1080\"")]
    [InlineData(1920, 0, 1280, 1024, "\"1920,0 1280x1024\"")]
    public void Rectangle_Serialize_ProducesCompactString(int x, int y, int w, int h, string expected)
    {
        var json = JsonSerializer.Serialize(new Rectangle(x, y, w, h), s_converterOpts);
        Assert.Equal(expected, json);
    }

    [Theory]
    [InlineData("\"0,0 1920x1080\"", 0, 0, 1920, 1080)]
    [InlineData("\"1920,0 1280x1024\"", 1920, 0, 1280, 1024)]
    public void Rectangle_Deserialize_RoundTrips(string json, int ex, int ey, int ew, int eh)
    {
        var rect = JsonSerializer.Deserialize<Rectangle>(json, s_converterOpts);
        Assert.Equal(new Rectangle(ex, ey, ew, eh), rect);
    }

    [Fact]
    public void Rectangle_NullableValue_SerializesAsString()
    {
        var device = new DeviceInfo { Id = "test", DisplayBounds = new Rectangle(0, 0, 2560, 1440) };
        var json = JsonSerializer.Serialize(device, DeviceInfoJsonContext.Default.DeviceInfo);
        Assert.Contains("\"displayBounds\":\"0,0 2560x1440\"", json);
    }
}

using System.Text.Json;

namespace Periphery.Tests;

/// <summary>
/// Pins the wire format of <see cref="DeviceActivityStatus"/> to the member
/// <b>name</b>, not the integer ordinal. ADR-0056 added <c>Unknown = 0</c> and
/// renumbered <c>Absent/Present/Active</c> to <c>1/2/3</c>; the
/// <c>[JsonConverter(typeof(JsonStringEnumConverter&lt;DeviceActivityStatus&gt;))]</c>
/// on the enum makes that renumber invisible to any serializer. These tests
/// fail loudly if the converter is ever dropped (the enum would silently
/// regress to integer serialization, shifting every persisted value).
/// </summary>
public class DeviceActivityStatusSerializationTests
{
    [Theory]
    [InlineData(DeviceActivityStatus.Unknown, "\"Unknown\"")]
    [InlineData(DeviceActivityStatus.Absent, "\"Absent\"")]
    [InlineData(DeviceActivityStatus.Present, "\"Present\"")]
    [InlineData(DeviceActivityStatus.Active, "\"Active\"")]
    public void Serializes_ByName_NotInteger(DeviceActivityStatus status, string expectedJson)
    {
        var json = JsonSerializer.Serialize(status);

        Assert.Equal(expectedJson, json);
    }

    [Theory]
    [InlineData(DeviceActivityStatus.Unknown)]
    [InlineData(DeviceActivityStatus.Absent)]
    [InlineData(DeviceActivityStatus.Present)]
    [InlineData(DeviceActivityStatus.Active)]
    public void RoundTrips_ByName(DeviceActivityStatus status)
    {
        var json = JsonSerializer.Serialize(status);
        var back = JsonSerializer.Deserialize<DeviceActivityStatus>(json);

        Assert.Equal(status, back);
    }

    [Fact]
    public void Deserializes_FromName()
    {
        Assert.Equal(DeviceActivityStatus.Unknown, JsonSerializer.Deserialize<DeviceActivityStatus>("\"Unknown\""));
        Assert.Equal(DeviceActivityStatus.Absent, JsonSerializer.Deserialize<DeviceActivityStatus>("\"Absent\""));
        Assert.Equal(DeviceActivityStatus.Present, JsonSerializer.Deserialize<DeviceActivityStatus>("\"Present\""));
        Assert.Equal(DeviceActivityStatus.Active, JsonSerializer.Deserialize<DeviceActivityStatus>("\"Active\""));
    }

    [Fact]
    public void DoesNotSerialize_AsBareInteger()
    {
        // Guards against the renumber regression: if the converter were dropped,
        // these would serialize as "0".."3" and this assertion would catch it.
        foreach (var status in Enum.GetValues<DeviceActivityStatus>())
        {
            var json = JsonSerializer.Serialize(status);
            Assert.StartsWith("\"", json);
            Assert.False(int.TryParse(json, out _), $"Expected name form, got integer: {json}");
        }
    }
}

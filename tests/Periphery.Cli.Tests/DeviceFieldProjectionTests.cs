using System.Collections.Immutable;
using System.Net;
using Periphery.Cli.Rendering;

namespace Periphery.Cli.Tests;

/// <summary>
/// Tests for the pure half of the verbose <c>devices list --verbose</c> dump:
/// <see cref="DeviceFieldProjection.Project(DeviceInfo)"/> decides which rows
/// survive elision and how each value is formatted, with no Spectre dependency
/// (functional core, ADR-0052). These assertions pin the regression-prone
/// projection logic that was previously untestable because it baked
/// <c>Spectre.Console.Tree</c> markup inline.
/// </summary>
public sealed class DeviceFieldProjectionTests
{
    private static DeviceField? Row(IReadOnlyList<DeviceField> rows, string label)
        => rows.FirstOrDefault(r => r.Label == label);

    // ── Empty / null elision ───────────────────────────────────────────

    [Fact]
    public void Project_OmitsNullProperties()
    {
        var rows = DeviceFieldProjection.Project(new DeviceInfo { Id = "USB\\X" });

        // Nullable properties left null are elided — no blank rows.
        Assert.Null(Row(rows, nameof(DeviceInfo.Name)));
        Assert.Null(Row(rows, nameof(DeviceInfo.Manufacturer)));
        Assert.Null(Row(rows, nameof(DeviceInfo.VendorId)));
        Assert.Null(Row(rows, nameof(DeviceInfo.SerialNumber)));
        Assert.Null(Row(rows, nameof(DeviceInfo.IPAddresses)));
    }

    [Fact]
    public void Project_OmitsEmptyTagsSetAndEmptyPropertyBag()
    {
        // Tags defaults to an empty ImmutableHashSet and Properties to an
        // empty ImmutableDictionary — both must be elided (ICollection.Count == 0),
        // not rendered as "(empty)" rows.
        var rows = DeviceFieldProjection.Project(new DeviceInfo { Id = "USB\\X" });

        Assert.Null(Row(rows, nameof(DeviceInfo.Tags)));
        Assert.Null(Row(rows, nameof(DeviceInfo.Properties)));
    }

    [Fact]
    public void Project_KeepsNonNullValueTypeDefaults()
    {
        // Non-nullable value-typed properties are never "empty": they always
        // render, even at their default (Category=All, Status=Unknown,
        // BusType=Unknown, IsActive=false). This is existing behavior the
        // split must preserve.
        var rows = DeviceFieldProjection.Project(new DeviceInfo { Id = "USB\\X" });

        Assert.Equal("All", Row(rows, nameof(DeviceInfo.Category))?.Value);
        Assert.Equal("Unknown", Row(rows, nameof(DeviceInfo.Status))?.Value);
        Assert.Equal("Unknown", Row(rows, nameof(DeviceInfo.BusType))?.Value);
        Assert.Equal("false", Row(rows, nameof(DeviceInfo.IsActive))?.Value);
    }

    [Fact]
    public void Project_LeafRowsHaveNoChildren()
    {
        var rows = DeviceFieldProjection.Project(new DeviceInfo { Id = "USB\\X" });
        var id = Row(rows, nameof(DeviceInfo.Id));

        Assert.NotNull(id);
        Assert.Empty(id!.Children);
    }

    // ── Typed Id / HardwareId render via their string form ─────────────

    [Fact]
    public void Project_DeviceId_RendersAsRawString()
    {
        // DeviceId is a strongly-typed struct; the verbose dump must show its
        // underlying instance-id string (DeviceId.ToString() => Value), not a
        // struct type name.
        const string raw = "USB\\VID_10C4&PID_8A7E\\6&ABCDEF";
        var rows = DeviceFieldProjection.Project(new DeviceInfo { Id = raw });

        Assert.Equal(raw, Row(rows, nameof(DeviceInfo.Id))?.Value);
    }

    [Fact]
    public void Project_HardwareId_RendersAsFourDigitHex()
    {
        // VendorId / ProductId are HardwareId structs; ToString() is "X4".
        var rows = DeviceFieldProjection.Project(new DeviceInfo
        {
            Id = "USB\\X",
            VendorId = new HardwareId(0x046D),
            ProductId = new HardwareId(0xC52B),
        });

        Assert.Equal("046D", Row(rows, nameof(DeviceInfo.VendorId))?.Value);
        Assert.Equal("C52B", Row(rows, nameof(DeviceInfo.ProductId))?.Value);
    }

    [Fact]
    public void Project_ParentId_RendersAsRawString()
    {
        // ParentId is a nullable DeviceId — when present it renders via its
        // string form like Id does.
        var rows = DeviceFieldProjection.Project(new DeviceInfo
        {
            Id = "USB\\CHILD",
            ParentId = "USB\\PARENT",
        });

        Assert.Equal("USB\\PARENT", Row(rows, nameof(DeviceInfo.ParentId))?.Value);
    }

    // ── IP-array formatting ────────────────────────────────────────────

    [Fact]
    public void Project_IPAddresses_FormatAsBracketedCommaList()
    {
        var rows = DeviceFieldProjection.Project(new DeviceInfo
        {
            Id = "NET\\X",
            IPAddresses = ImmutableArray.Create(
                IPAddress.Parse("192.168.1.10"),
                IPAddress.Parse("10.0.0.1")),
        });

        Assert.Equal("[192.168.1.10, 10.0.0.1]", Row(rows, nameof(DeviceInfo.IPAddresses))?.Value);
    }

    [Fact]
    public void Project_Tags_FormatAsBracketedCommaList()
    {
        var rows = DeviceFieldProjection.Project(new DeviceInfo
        {
            Id = "HID\\X",
            Tags = ImmutableHashSet.Create("Hid", "Battery"),
        });

        var tags = Row(rows, nameof(DeviceInfo.Tags));
        Assert.NotNull(tags);
        // Set iteration order is not guaranteed; assert shape + membership.
        Assert.StartsWith("[", tags!.Value);
        Assert.EndsWith("]", tags.Value);
        Assert.Contains("Hid", tags.Value!);
        Assert.Contains("Battery", tags.Value!);
    }

    // ── Property bag nests as a group of key/value children ────────────

    [Fact]
    public void Project_PropertyBag_NestsAsGroupWithChildren()
    {
        var rows = DeviceFieldProjection.Project(new DeviceInfo
        {
            Id = "USB\\X",
            Properties = new Dictionary<string, object?>
            {
                [WellKnownProperties.RawStatus] = 22,
                [WellKnownProperties.HardwareIds] = new[] { "USB\\VID_046D&PID_C52B", "USB\\VID_046D" },
            }.ToImmutableDictionary(),
        });

        var bag = Row(rows, nameof(DeviceInfo.Properties));
        Assert.NotNull(bag);

        // Group row: no value of its own, one child per bag entry.
        Assert.Null(bag!.Value);
        Assert.Equal(2, bag.Children.Count);

        var rawStatus = bag.Children.FirstOrDefault(c => c.Label == WellKnownProperties.RawStatus);
        Assert.NotNull(rawStatus);
        Assert.Equal("22", rawStatus!.Value);
        Assert.Empty(rawStatus.Children);

        // string[] bag value formats as a bracketed comma list.
        var hwIds = bag.Children.FirstOrDefault(c => c.Label == WellKnownProperties.HardwareIds);
        Assert.NotNull(hwIds);
        Assert.Equal("[USB\\VID_046D&PID_C52B, USB\\VID_046D]", hwIds!.Value);
    }

    [Fact]
    public void Project_PropertyBag_NullChildValueFormatsAsNullToken()
    {
        var rows = DeviceFieldProjection.Project(new DeviceInfo
        {
            Id = "USB\\X",
            Properties = new Dictionary<string, object?> { ["Missing"] = null }
                .ToImmutableDictionary(),
        });

        var bag = Row(rows, nameof(DeviceInfo.Properties));
        Assert.NotNull(bag);
        var missing = bag!.Children.Single();
        Assert.Equal("Missing", missing.Label);
        Assert.Equal("(null)", missing.Value);
    }

    // ── Scalar formatting parity ───────────────────────────────────────

    [Fact]
    public void Project_Name_RendersVerbatim()
    {
        var rows = DeviceFieldProjection.Project(new DeviceInfo
        {
            Id = "USB\\X",
            Name = "Logitech USB Receiver",
        });

        Assert.Equal("Logitech USB Receiver", Row(rows, nameof(DeviceInfo.Name))?.Value);
    }
}

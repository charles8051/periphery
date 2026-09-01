using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Periphery.Tests;

public class DeviceFilterSpecTests
{
    // ── Parity with DeviceFilter ───────────────────────────────────────

    /// <summary>
    /// Criteria on <see cref="DeviceFilter"/> that deliberately have no spec
    /// property. Keyed by method name because overloads share one property.
    /// </summary>
    private static readonly Dictionary<string, string> NotExpressibleAsData = new(
        StringComparer.Ordinal
    )
    {
        [nameof(DeviceFilter.Where)] =
            "Takes a delegate. Excluded by construction, not by omission — this is the only one.",
        [nameof(DeviceFilter.Apply)] = "The replay itself, not a criterion.",
    };

    /// <summary>Method name on DeviceFilter -> the spec property that replays it.</summary>
    private static readonly Dictionary<string, string[]> CriterionToProperties = new(
        StringComparer.Ordinal
    )
    {
        [nameof(DeviceFilter.OfCategory)] = [nameof(DeviceFilterSpec.Category)],
        [nameof(DeviceFilter.WithTag)] = [nameof(DeviceFilterSpec.AllTags)],
        [nameof(DeviceFilter.WithAllTags)] = [nameof(DeviceFilterSpec.AllTags)],
        [nameof(DeviceFilter.WithAnyTag)] = [nameof(DeviceFilterSpec.AnyTags)],
        [nameof(DeviceFilter.WithName)] = [nameof(DeviceFilterSpec.DeviceName)],
        [nameof(DeviceFilter.ByManufacturer)] = [nameof(DeviceFilterSpec.Manufacturer)],
        [nameof(DeviceFilter.WithDriver)] = [nameof(DeviceFilterSpec.Driver)],
        [nameof(DeviceFilter.WithUsbId)] =
        [
            nameof(DeviceFilterSpec.VendorId),
            nameof(DeviceFilterSpec.ProductId),
        ],
        [nameof(DeviceFilter.WithSerialNumber)] = [nameof(DeviceFilterSpec.SerialNumber)],
        [nameof(DeviceFilter.WithId)] = [nameof(DeviceFilterSpec.Id)],
        [nameof(DeviceFilter.WithIdStartsWith)] = [nameof(DeviceFilterSpec.IdStartsWith)],
        [nameof(DeviceFilter.WithParent)] = [nameof(DeviceFilterSpec.ParentId)],
        [nameof(DeviceFilter.WithContainerId)] = [nameof(DeviceFilterSpec.ContainerId)],
        [nameof(DeviceFilter.WithMacAddress)] = [nameof(DeviceFilterSpec.MacAddress)],
        [nameof(DeviceFilter.WithPortName)] = [nameof(DeviceFilterSpec.PortName)],
        [nameof(DeviceFilter.WithBusType)] = [nameof(DeviceFilterSpec.BusType)],
        [nameof(DeviceFilter.WithStatus)] = [nameof(DeviceFilterSpec.Status)],
        [nameof(DeviceFilter.WithDriveType)] = [nameof(DeviceFilterSpec.DriveType)],
        [nameof(DeviceFilter.WithUsbSpeed)] = [nameof(DeviceFilterSpec.UsbSpeed)],
        [nameof(DeviceFilter.WithBatteryStatus)] = [nameof(DeviceFilterSpec.BatteryStatus)],
        [nameof(DeviceFilter.Active)] = [nameof(DeviceFilterSpec.Active)],
        [nameof(DeviceFilter.PhysicalOnly)] = [nameof(DeviceFilterSpec.Physicality)],
        [nameof(DeviceFilter.VirtualOnly)] = [nameof(DeviceFilterSpec.Physicality)],
        [nameof(DeviceFilter.WithMinResolution)] =
        [
            nameof(DeviceFilterSpec.MinWidth),
            nameof(DeviceFilterSpec.MinHeight),
        ],
    };

    private static string[] FilterCriterionNames() =>
        [
            .. typeof(DeviceFilter)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.ReturnType == typeof(DeviceFilter) && !m.IsSpecialName)
                .Select(m => m.Name)
                .Distinct(StringComparer.Ordinal),
        ];

    [Fact]
    public void EveryCriterion_HasASpecProperty_OrAWrittenReasonItCannot()
    {
        var unmapped = FilterCriterionNames()
            .Where(n =>
                !CriterionToProperties.ContainsKey(n) && !NotExpressibleAsData.ContainsKey(n)
            )
            .ToList();

        Assert.True(
            unmapped.Count == 0,
            "DeviceFilter declares criteria with no DeviceFilterSpec property. Add one, or add an "
                + $"entry to {nameof(NotExpressibleAsData)} saying why it cannot be data:"
                + $"{Environment.NewLine}  "
                + string.Join(Environment.NewLine + "  ", unmapped)
        );
    }

    [Fact]
    public void EveryMappedProperty_ActuallyExistsOnTheSpec()
    {
        var specProps = typeof(DeviceFilterSpec)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var (criterion, properties) in CriterionToProperties)
        foreach (var property in properties)
            Assert.True(
                specProps.Contains(property),
                $"The map says {criterion} replays as DeviceFilterSpec.{property}, which does not exist."
            );
    }

    [Fact]
    public void EveryStaleMapping_IsRejected()
    {
        var criteria = FilterCriterionNames().ToHashSet(StringComparer.Ordinal);
        foreach (var name in CriterionToProperties.Keys.Concat(NotExpressibleAsData.Keys))
            Assert.True(
                criteria.Contains(name),
                $"'{name}' is mapped but DeviceFilter no longer declares it. Remove the stale entry."
            );
    }

    // ── HasAnyCriteria agrees with what Apply replays ──────────────────

    public static TheoryData<DeviceFilterSpec> NonEmptySpecs =>
        [
            new() { Category = DeviceCategory.Usb },
            new() { AllTags = ["Printer"] },
            new() { AnyTags = ["Printer", "Imaging"] },
            new() { DeviceName = "Mouse" },
            new() { Manufacturer = "Logitech" },
            new() { Driver = "usbccgp" },
            new() { VendorId = "046D" },
            new() { SerialNumber = "ABC123" },
            new() { Id = "USB\\VID_046D" },
            new() { IdStartsWith = "DISPLAY\\" },
            new() { ParentId = "PCI\\VEN_8086" },
            new() { ContainerId = Guid.NewGuid() },
            new() { MacAddress = "00-11-22-33-44-55" },
            new() { PortName = "COM3" },
            new() { BusType = Periphery.BusType.USB },
            new() { Status = DeviceStatus.OK },
            new() { DriveType = System.IO.DriveType.Fixed },
            new() { UsbSpeed = Periphery.UsbSpeed.High },
            new() { BatteryStatus = Periphery.BatteryStatus.Charging },
            new() { Active = true },
            new() { Physicality = DevicePhysicality.Virtual },
            new() { MinWidth = 1920, MinHeight = 1080 },
        ];

    [Theory]
    [MemberData(nameof(NonEmptySpecs))]
    public void HasAnyCriteria_AgreesWithTheFilterApplyProduces(DeviceFilterSpec spec)
    {
        Assert.True(spec.HasAnyCriteria);

        // A filter that matches everything is what an empty spec would give.
        var everything = new DeviceInfo { Id = "x" };
        var filter = new DeviceFilter().Apply(spec);
        Assert.False(
            filter.Matches(everything) && spec.HasAnyCriteria && IsRestrictive(spec),
            $"Spec reports criteria but the filter it produced matches a bare device: {spec}"
        );
    }

    // Category/Active/Physicality can legitimately match a bare DeviceInfo.
    private static bool IsRestrictive(DeviceFilterSpec spec) =>
        spec.Category is null && spec.Active is null && spec.Physicality is null;

    [Fact]
    public void EmptySpec_HasNoCriteria_AndDescribesItself()
    {
        var spec = new DeviceFilterSpec();
        Assert.False(spec.HasAnyCriteria);
        Assert.Equal("(no criteria)", spec.ToString());
    }

    [Fact]
    public void EmptyTagArrays_AreNotCriteria()
    {
        var spec = new DeviceFilterSpec { AllTags = [], AnyTags = [] };
        Assert.False(spec.HasAnyCriteria);

        // And because they are not criteria, this spec is empty — so Apply
        // refuses it rather than yielding a match-everything filter.
        Assert.Throws<ArgumentException>(() => new DeviceFilter().Apply(spec));

        // The skip itself is what matters: WithAnyTag([]) called directly means
        // "match nothing", which an absent config value must not mean.
        var withOther = new DeviceFilterSpec { AnyTags = [], Category = DeviceCategory.Usb };
        var filter = new DeviceFilter().Apply(withOther);
        Assert.True(filter.Matches(new DeviceInfo { Id = "x", Category = DeviceCategory.Usb }));
    }

    // ── Replay preserves the provider hints ────────────────────────────

    [Fact]
    public void Apply_GoesThroughTheTypedMethods_SoProviderHintsSurvive()
    {
        var spec = new DeviceFilterSpec { Category = DeviceCategory.Monitor };
        var filter = new DeviceFilter().Apply(spec);

        // Category is the hint the Windows provider uses for class-GUID pushdown.
        // A hand-rolled Where(d => d.Category == ...) would match identically and
        // silently turn this into a full-system scan.
        Assert.True(filter.NeedsMonitorEnrichment);
        Assert.False(filter.NeedsBatteryEnrichment);
    }

    [Fact]
    public void Apply_TagsPopulateRelevantTags()
    {
        var filter = new DeviceFilter().Apply(new DeviceFilterSpec { AllTags = ["Printer"] });
        Assert.Contains("Printer", filter.RelevantTags);
    }

    // ── Bad values throw, naming the property ──────────────────────────

    [Theory]
    [InlineData(nameof(DeviceFilterSpec.MacAddress))]
    [InlineData(nameof(DeviceFilterSpec.PortName))]
    [InlineData(nameof(DeviceFilterSpec.VendorId))]
    public void Apply_ThrowsNamingTheProperty_OnAnUnparseableValue(string property)
    {
        DeviceFilterSpec spec = property switch
        {
            nameof(DeviceFilterSpec.MacAddress) => new() { MacAddress = "not-a-mac" },
            nameof(DeviceFilterSpec.PortName) => new() { PortName = "  " },
            _ => new() { VendorId = "ZZZZ" },
        };

        var ex = Assert.Throws<ArgumentException>(() => new DeviceFilter().Apply(spec));
        Assert.Contains(property, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_Throws_WhenProductIdHasNoVendorId()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new DeviceFilter().Apply(new DeviceFilterSpec { ProductId = "C52B" })
        );
        Assert.Contains(nameof(DeviceFilterSpec.VendorId), ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1920, null)]
    [InlineData(null, 1080)]
    public void Apply_Throws_WhenResolutionIsHalfSet(int? width, int? height)
    {
        var spec = new DeviceFilterSpec { MinWidth = width, MinHeight = height };
        Assert.Throws<ArgumentException>(() => new DeviceFilter().Apply(spec));
    }

    // ── DeviceProfile.FromSpec ─────────────────────────────────────────

    [Fact]
    public void FromSpec_Throws_OnAnEmptySpec_WithoutNamingADelegateParameter()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            DeviceProfile.FromSpec(new DeviceFilterSpec(), name: "FrontCamera")
        );
        Assert.Contains("FrontCamera", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("configure", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromSpec_BuildsAMatchingProfile()
    {
        var profile = DeviceProfile.FromSpec(
            new DeviceFilterSpec { Category = DeviceCategory.Usb, SerialNumber = "ABC123" },
            name: "Scanner"
        );

        Assert.Equal("Scanner", profile.Name);
        Assert.True(
            profile.Filter.Matches(
                new DeviceInfo
                {
                    Id = "x",
                    Category = DeviceCategory.Usb,
                    SerialNumber = "ABC123",
                }
            )
        );
        Assert.False(
            profile.Filter.Matches(
                new DeviceInfo
                {
                    Id = "x",
                    Category = DeviceCategory.Usb,
                    SerialNumber = "OTHER",
                }
            )
        );
    }

    // ── Equality is value-based, including tags ────────────────────────

    [Fact]
    public void TwoSpecsWithEqualTagContents_AreEqual()
    {
        // The compiler-generated record Equals compares string[] by REFERENCE,
        // so this is exactly the case ADR-0047 hit on DeviceInfo.Tags.
        var a = new DeviceFilterSpec { AllTags = ["Printer", "Imaging"] };
        var b = new DeviceFilterSpec { AllTags = ["Imaging", "Printer"] };

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void NullAndEmptyTagArrays_AreTheSameAbsentCriterion()
    {
        Assert.Equal(
            new DeviceFilterSpec { AllTags = null },
            new DeviceFilterSpec { AllTags = [] }
        );
    }

    [Fact]
    public void DifferentTagContents_AreNotEqual()
    {
        Assert.NotEqual(
            new DeviceFilterSpec { AllTags = ["Printer"] },
            new DeviceFilterSpec { AllTags = ["Imaging"] }
        );
    }

    // ── Binding ────────────────────────────────────────────────────────

    [Fact]
    public void BindsFromConfiguration_WithNoAdapter()
    {
        var json = """
            {
              "Category": "Camera",
              "DeviceName": "Integrated",
              "AllTags": [ "Imaging" ],
              "Active": true,
              "Physicality": "Physical",
              "MinWidth": 1280,
              "MinHeight": 720
            }
            """;

        var config = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();

        var spec = config.Get<DeviceFilterSpec>();

        Assert.NotNull(spec);
        Assert.Equal(DeviceCategory.Camera, spec!.Category);
        Assert.Equal("Integrated", spec.DeviceName);
        Assert.Equal(["Imaging"], spec.AllTags);
        Assert.True(spec.Active);
        Assert.Equal(DevicePhysicality.Physical, spec.Physicality);
        Assert.Equal(1280, spec.MinWidth);
        Assert.Equal(720, spec.MinHeight);
    }

    [Fact]
    public void RoundTripsThroughSourceGeneratedJson()
    {
        var spec = new DeviceFilterSpec
        {
            Category = DeviceCategory.Usb,
            AllTags = ["Printer"],
            VendorId = "046D",
            ProductId = "C52B",
            Active = true,
            Physicality = DevicePhysicality.Virtual,
            DriveType = System.IO.DriveType.Fixed,
            ContainerId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
        };

        var json = JsonSerializer.Serialize(
            spec,
            DeviceFilterSpecJsonContext.Default.DeviceFilterSpec
        );
        var back = JsonSerializer.Deserialize(
            json,
            DeviceFilterSpecJsonContext.Default.DeviceFilterSpec
        );

        Assert.Equal(spec, back);
    }

    [Fact]
    public void EnumsSerialiseByName_NotAsIntegers()
    {
        var json = JsonSerializer.Serialize(
            new DeviceFilterSpec
            {
                DriveType = System.IO.DriveType.Fixed,
                Category = DeviceCategory.Usb,
            },
            DeviceFilterSpecJsonContext.Default.DeviceFilterSpec
        );

        // DriveType is a BCL enum with no type-level converter, so without the
        // property-level one it would emit 3 and disagree with IConfiguration,
        // which binds it by name.
        Assert.Contains("\"Fixed\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Usb\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void HasAnyCriteria_IsNotSerialised()
    {
        var json = JsonSerializer.Serialize(
            new DeviceFilterSpec { Category = DeviceCategory.Usb },
            DeviceFilterSpecJsonContext.Default.DeviceFilterSpec
        );
        Assert.DoesNotContain("asAnyCriteria", json, StringComparison.Ordinal);
    }

    [Fact]
    public void AMisspelledMemberThrows_RatherThanBindingToAnEmptySpec()
    {
        // Without JsonUnmappedMemberHandling.Disallow this deserialises to a
        // spec with no criteria — which, handed to a filter, matches every
        // device on the box. Silence is the worst outcome here.
        var json = """{ "catgory": "Usb" }""";

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize(json, DeviceFilterSpecJsonContext.Default.DeviceFilterSpec)
        );
    }

    [Fact]
    public void Apply_RefusesAnEmptySpec_RatherThanMatchingEveryDevice()
    {
        // The fail-open this closes: a mistyped configuration binds to an empty
        // spec, and an empty spec applied to a fresh filter matches everything.
        var ex = Assert.Throws<ArgumentException>(() =>
            new DeviceFilter().Apply(new DeviceFilterSpec())
        );
        Assert.Contains("no criteria", ex.Message, StringComparison.Ordinal);

        Assert.Throws<ArgumentException>(() => Devices.Enumerate().Apply(new DeviceFilterSpec()));
    }

    [Fact]
    public void ConfigurationSilentlyIgnoresAnUnknownKey_UnlessAskedNotTo()
    {
        // JsonUnmappedMemberHandling is a System.Text.Json attribute and means
        // nothing to IConfiguration. A misspelled key therefore binds to a spec
        // that quietly drops the criterion — a filter matching MORE devices than
        // intended. The type's docs say so, and this pins both halves.
        var json = """{ "Category": "Camera", "Catgory": "Usb" }""";
        var config = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();

        var lenient = config.Get<DeviceFilterSpec>();
        Assert.Equal(DeviceCategory.Camera, lenient!.Category);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            config.Get<DeviceFilterSpec>(o => o.ErrorOnUnknownConfiguration = true)
        );
        Assert.Contains("Catgory", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryEnumProperty_DeserialisesByName_ThroughTheGeneratedContext()
    {
        // Only DriveType needed a property-level converter; the five Periphery
        // enums carry a type-level one that survives Nullable<T> under source
        // generation. This is the documented JSON shape, so it must round-trip.
        var json = """
            {"category":"Usb","busType":"USB","status":"OK","usbSpeed":"High",
             "batteryStatus":"Charging","driveType":"Fixed","physicality":"Virtual"}
            """;

        var spec = JsonSerializer.Deserialize(
            json,
            DeviceFilterSpecJsonContext.Default.DeviceFilterSpec
        );

        Assert.NotNull(spec);
        Assert.Equal(DeviceCategory.Usb, spec!.Category);
        Assert.Equal(Periphery.BusType.USB, spec.BusType);
        Assert.Equal(DeviceStatus.OK, spec.Status);
        Assert.Equal(Periphery.UsbSpeed.High, spec.UsbSpeed);
        Assert.Equal(Periphery.BatteryStatus.Charging, spec.BatteryStatus);
        Assert.Equal(System.IO.DriveType.Fixed, spec.DriveType);
        Assert.Equal(DevicePhysicality.Virtual, spec.Physicality);
    }

    [Fact]
    public void DuplicateTags_AreEqualToTheDistinctSet()
    {
        // Duplicates replay identically, so set semantics must ignore them.
        var a = new DeviceFilterSpec { AllTags = ["Printer"] };
        var b = new DeviceFilterSpec { AllTags = ["Printer", "Printer"] };

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void TheTwoJsonContexts_AgreeOnOptions()
    {
        var spec = DeviceFilterSpecJsonContext.Default.Options;
        var info = DeviceInfoJsonContext.Default.Options;

        Assert.Equal(info.PropertyNamingPolicy, spec.PropertyNamingPolicy);
        Assert.Equal(info.DefaultIgnoreCondition, spec.DefaultIgnoreCondition);
        Assert.Equal(info.WriteIndented, spec.WriteIndented);
    }
}

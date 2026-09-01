using System.Reflection;

namespace Periphery.Tests;

/// <summary>
/// Guards the three fluent criteria surfaces — <see cref="DeviceFilter"/>,
/// <see cref="DeviceQuery"/> and <see cref="DeviceWatcher"/> — against silent
/// drift.
/// </summary>
/// <remarks>
/// <para>
/// These types re-declare the same vocabulary by hand, and they had already
/// drifted apart before this test existed. A shared
/// <c>IDeviceCriteria&lt;TSelf&gt;</c> interface was considered and rejected: the
/// compiler enforces only arity, parameter types and return type. Default
/// values, <c>params</c>, parameter names and nullability are <b>not</b>
/// checked, and an implementation may declare a different default with no
/// diagnostic at all — after which the same call binds differently depending on
/// whether it goes through the concrete type or the interface. Those are
/// precisely the axes that drift, so they are asserted here instead.
/// </para>
/// <para>
/// <b>Exclusions are deliberate and each one carries its reason.</b> A method
/// missing from a surface is either a bug this test catches or a decision
/// recorded in <see cref="Excluded"/> — there is no third case.
/// </para>
/// </remarks>
public class CriteriaSurfaceParityTests
{
    /// <summary>
    /// Criteria on <see cref="DeviceFilter"/> that intentionally have no
    /// counterpart on one of the other surfaces, and why.
    /// </summary>
    private static readonly Dictionary<string, string> Excluded = new()
    {
        // A watcher's filter is evaluated against post-transition state: the
        // Deactivated handler runs when IsActive is already false, so a watcher
        // filtered Active(true) would suppress the very event being watched for.
        [$"{nameof(DeviceWatcher)}.Active"] =
            "A watcher filter runs against post-transition state; Active(true) would suppress Deactivated.",

        // Tags come from the enrichment pipeline. The Windows monitor provider
        // seeds its last-known cache with the unenriched build and that record
        // is what a removal carries, so a tag-filtered watcher would see
        // Appeared and never Disappeared. Linux/macOS enrich in their single
        // device build, so the feature would also be platform-divergent.
        [$"{nameof(DeviceWatcher)}.{nameof(DeviceFilter.WithTag)}"] =
            "Tags are enrichment-only; the Windows removal payload is unenriched, so Disappeared would never match.",
        [$"{nameof(DeviceWatcher)}.{nameof(DeviceFilter.WithAllTags)}"] = "See WithTag.",
        [$"{nameof(DeviceWatcher)}.{nameof(DeviceFilter.WithAnyTag)}"] = "See WithTag.",
        // A DeviceFilterSpec can carry Active, AllTags and AnyTags — the three
        // criteria a watcher must not honour, per the entries above. A
        // watcher-level Apply would therefore either ignore four properties
        // silently or throw on specs that are perfectly valid elsewhere. Bind a
        // spec to a filter or a profile and hand the watcher a tracker instead.
        [$"{nameof(DeviceWatcher)}.{nameof(DeviceFilter.Apply)}"] =
            "A spec can carry Active/AllTags/AnyTags, which a watcher cannot honour; it would ignore them silently.",
    };

    /// <summary>
    /// Shared context for reading nullable-reference annotations. Not
    /// thread-safe, and xUnit runs the methods of one class serially, so one
    /// instance for the class is correct.
    /// </summary>
    private static readonly NullabilityInfoContext Nullability = new();

    private static MethodInfo[] CriteriaOf(Type t) =>
        [
            .. t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.ReturnType == t && !m.IsSpecialName),
        ];

    public static TheoryData<Type> Surfaces => new() { typeof(DeviceQuery), typeof(DeviceWatcher) };

    [Theory]
    [MemberData(nameof(Surfaces))]
    public void EveryDeviceFilterCriterion_IsPresentOrExplicitlyExcluded(Type surface)
    {
        var missing = new List<string>();

        foreach (var source in CriteriaOf(typeof(DeviceFilter)))
        {
            var key = $"{surface.Name}.{source.Name}";
            if (Excluded.ContainsKey(key))
                continue;

            var sourceParams = source.GetParameters().Select(p => p.ParameterType).ToArray();
            var match = surface.GetMethod(
                source.Name,
                BindingFlags.Public | BindingFlags.Instance,
                binder: null,
                types: sourceParams,
                modifiers: null
            );

            if (match is null || match.ReturnType != surface)
                missing.Add($"{key}({string.Join(", ", sourceParams.Select(p => p.Name))})");
        }

        Assert.True(
            missing.Count == 0,
            $"{surface.Name} is missing criteria that DeviceFilter declares. Either forward them, "
                + $"or add an entry to {nameof(Excluded)} saying why not:{Environment.NewLine}  "
                + string.Join(Environment.NewLine + "  ", missing)
        );
    }

    /// <summary>
    /// The drift a shared interface would <b>not</b> have caught: a forwarder
    /// that declares a different default value, drops <c>params</c>, or renames
    /// a parameter. Each of those changes behaviour or breaks named arguments
    /// while still satisfying an interface contract.
    /// </summary>
    [Theory]
    [MemberData(nameof(Surfaces))]
    public void ForwardedCriteria_MatchDefaultsParamsAndParameterNames(Type surface)
    {
        var mismatches = new List<string>();

        foreach (var source in CriteriaOf(typeof(DeviceFilter)))
        {
            var sourceParams = source.GetParameters();
            var match = surface.GetMethod(
                source.Name,
                BindingFlags.Public | BindingFlags.Instance,
                binder: null,
                types: [.. sourceParams.Select(p => p.ParameterType)],
                modifiers: null
            );

            if (match is null)
                continue; // absence is the other test's business

            foreach (var (want, got) in sourceParams.Zip(match.GetParameters()))
            {
                var where = $"{surface.Name}.{source.Name}({want.Name})";

                if (want.Name != got.Name)
                    mismatches.Add(
                        $"{where}: parameter named '{got.Name}' on the forwarder, '{want.Name}' on DeviceFilter"
                    );

                if (want.HasDefaultValue != got.HasDefaultValue)
                    mismatches.Add($"{where}: default value present on one side only");
                else if (want.HasDefaultValue && !Equals(want.DefaultValue, got.DefaultValue))
                    mismatches.Add(
                        $"{where}: default is '{got.DefaultValue}' on the forwarder, '{want.DefaultValue}' on DeviceFilter"
                    );

                var wantParams = want.IsDefined(typeof(ParamArrayAttribute), inherit: false);
                var gotParams = got.IsDefined(typeof(ParamArrayAttribute), inherit: false);
                if (wantParams != gotParams)
                    mismatches.Add($"{where}: params modifier present on one side only");

                // Nullability is erased from ParameterType, so string and string?
                // are the same runtime type and everything above would pass a
                // forwarder that changed one to the other. NullabilityInfoContext
                // reads the annotation attributes the compiler emitted.
                var wantNull = Nullability.Create(want).WriteState;
                var gotNull = Nullability.Create(got).WriteState;
                if (wantNull != gotNull)
                    mismatches.Add(
                        $"{where}: nullability is {gotNull} on the forwarder, {wantNull} on DeviceFilter"
                    );
            }
        }

        Assert.True(
            mismatches.Count == 0,
            "Forwarded criteria must match DeviceFilter exactly on the axes a shared interface "
                + $"would not enforce:{Environment.NewLine}  "
                + string.Join(Environment.NewLine + "  ", mismatches)
        );
    }

    [Fact]
    public void EveryExclusion_NamesAMethodThatActuallyExists()
    {
        var filterNames = CriteriaOf(typeof(DeviceFilter))
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var key in Excluded.Keys)
        {
            var methodName = key[(key.IndexOf('.') + 1)..];
            Assert.True(
                filterNames.Contains(methodName),
                $"Exclusion '{key}' names '{methodName}', which DeviceFilter no longer declares. "
                    + "Remove the stale entry."
            );
        }
    }

    /// <summary>
    /// An exclusion suppresses the parity check for one member on one surface.
    /// If that member is later added anyway, the exclusion would go on
    /// suppressing the check forever — and silently, on exactly the members that
    /// are absent because adding them is a bug (see the tag entries).
    /// </summary>
    [Fact]
    public void EveryExclusion_DescribesAMemberThatIsActuallyAbsent()
    {
        var surfaces = new[] { typeof(DeviceQuery), typeof(DeviceWatcher) }.ToDictionary(
            t => t.Name,
            StringComparer.Ordinal
        );

        foreach (var (key, reason) in Excluded)
        {
            var split = key.IndexOf('.');
            var surfaceName = key[..split];
            var methodName = key[(split + 1)..];

            Assert.True(
                surfaces.TryGetValue(surfaceName, out var surface),
                $"Exclusion '{key}' names surface '{surfaceName}', which is not one of the "
                    + $"surfaces under test ({string.Join(", ", surfaces.Keys)})."
            );

            var present = surface!
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Any(m => m.Name == methodName && m.ReturnType == surface && !m.IsSpecialName);

            Assert.False(
                present,
                $"{surfaceName}.{methodName} now exists, but an exclusion still says it should not:"
                    + $"{Environment.NewLine}  \"{reason}\"{Environment.NewLine}"
                    + "Either that reason no longer holds — delete the exclusion so parity is "
                    + "enforced — or the method was added in error and should be removed."
            );
        }
    }
}

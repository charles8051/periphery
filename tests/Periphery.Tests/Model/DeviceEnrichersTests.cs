namespace Periphery.Tests;

/// <summary>
/// Behaviour pinning for the <see cref="DeviceEnrichers"/> registry that
/// underpins the ADR-0024 §3c auto-registration hook. Tests serialise on
/// <see cref="DeviceEnrichersTestCollection"/> because the registry is
/// process-wide; xUnit's default class-level parallelism would race
/// Register / Unregister calls.
/// </summary>
[Collection(nameof(DeviceEnrichersTestCollection))]
public class DeviceEnrichersTests
{
    [Fact]
    public void Register_AddsEnricherToSnapshot()
    {
        var enricher = new FakeEnricher();
        try
        {
            DeviceEnrichers.Register(enricher);
            Assert.Contains(enricher, DeviceEnrichers.Snapshot());
        }
        finally { DeviceEnrichers.Unregister(enricher); }
    }

    [Fact]
    public void Register_Idempotent_SameInstanceTwice_StaysOnce()
    {
        var enricher = new FakeEnricher();
        try
        {
            DeviceEnrichers.Register(enricher);
            DeviceEnrichers.Register(enricher);
            int count = DeviceEnrichers.Snapshot().Count(e => ReferenceEquals(e, enricher));
            Assert.Equal(1, count);
        }
        finally { DeviceEnrichers.Unregister(enricher); }
    }

    [Fact]
    public void Unregister_RemovesEnricher_ReturnsTrue()
    {
        var enricher = new FakeEnricher();
        DeviceEnrichers.Register(enricher);

        bool removed = DeviceEnrichers.Unregister(enricher);

        Assert.True(removed);
        Assert.DoesNotContain(enricher, DeviceEnrichers.Snapshot());
    }

    [Fact]
    public void Unregister_NotRegistered_ReturnsFalse()
    {
        var enricher = new FakeEnricher();
        Assert.False(DeviceEnrichers.Unregister(enricher));
    }

    [Fact]
    public void Snapshot_ReturnsImmutableArray()
    {
        // Snapshot is an ImmutableArray<T>; calls don't expose mutable
        // state that subsequent Register / Unregister could change
        // behind the caller's back.
        var before = DeviceEnrichers.Snapshot();
        var fresh = new FakeEnricher();

        try
        {
            DeviceEnrichers.Register(fresh);
            var after = DeviceEnrichers.Snapshot();

            Assert.DoesNotContain(fresh, before);   // first snapshot frozen
            Assert.Contains(fresh, after);
        }
        finally { DeviceEnrichers.Unregister(fresh); }
    }

    [Fact]
    public void Register_NullEnricher_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => DeviceEnrichers.Register(null!));
    }

    [Fact]
    public void Unregister_NullEnricher_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => DeviceEnrichers.Unregister(null!));
    }

    // ── ScopeForTags (ADR-0051 §5) ──────────────────────────────────────

    [Fact]
    public void ScopeForTags_EmptyTagSet_ReturnsNone()
    {
        Assert.Same(EnricherScope.None, DeviceEnrichers.ScopeForTags(new HashSet<string>()));
    }

    [Fact]
    public void ScopeForTags_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => DeviceEnrichers.ScopeForTags(null!));
    }

    [Fact]
    public void ScopeForTags_NoEmitterForTag_ReturnsEmpty()
    {
        // A tag no registered enricher claims yields no scope.
        Assert.True(DeviceEnrichers.ScopeForTags(Tags("ScopeTest.Unclaimed")).IsEmpty);
    }

    [Fact]
    public void ScopeForTags_UnionsScopeOfMatchingEmitters()
    {
        var a = new FakeTagEnricher("ScopeTest.Tag", new EnricherScope(["{guid-a}"], ["subA"], ["ClassA"]));
        var b = new FakeTagEnricher("ScopeTest.Tag", new EnricherScope(["{guid-b}"], ["subB"], []));
        try
        {
            DeviceEnrichers.Register(a);
            DeviceEnrichers.Register(b);

            var scope = DeviceEnrichers.ScopeForTags(Tags("ScopeTest.Tag"));

            Assert.Equal(["{guid-a}", "{guid-b}"], scope.WindowsClassGuids.Order());
            Assert.Equal(["subA", "subB"], scope.LinuxSubsystems.Order());
            Assert.Equal(["ClassA"], scope.MacOSClasses.Order());
        }
        finally
        {
            DeviceEnrichers.Unregister(a);
            DeviceEnrichers.Unregister(b);
        }
    }

    [Fact]
    public void ScopeForTags_IgnoresEnrichersThatDontEmitTheTag()
    {
        var matching = new FakeTagEnricher("ScopeTest.Wanted", new EnricherScope(["{wanted}"], [], []));
        var other = new FakeTagEnricher("ScopeTest.Other", new EnricherScope(["{other}"], [], []));
        try
        {
            DeviceEnrichers.Register(matching);
            DeviceEnrichers.Register(other);

            var scope = DeviceEnrichers.ScopeForTags(Tags("ScopeTest.Wanted"));

            Assert.Equal(["{wanted}"], scope.WindowsClassGuids);
        }
        finally
        {
            DeviceEnrichers.Unregister(matching);
            DeviceEnrichers.Unregister(other);
        }
    }

    [Fact]
    public void ScopeForTags_IgnoresPlainEnrichers_WithoutTagMetadata()
    {
        // A plain IDeviceEnricher (not ITagEmittingEnricher) contributes no scope.
        var plain = new FakeEnricher();
        try
        {
            DeviceEnrichers.Register(plain);
            Assert.True(DeviceEnrichers.ScopeForTags(Tags("ScopeTest.Anything")).IsEmpty);
        }
        finally { DeviceEnrichers.Unregister(plain); }
    }

    private static IReadOnlySet<string> Tags(params string[] tags) =>
        new HashSet<string>(tags, StringComparer.Ordinal);

    private sealed class FakeTagEnricher(string emits, EnricherScope scope) : ITagEmittingEnricher
    {
        public IReadOnlySet<string> EmitsTags { get; } =
            new HashSet<string>(StringComparer.Ordinal) { emits };
        public EnricherScope Scope { get; } = scope;
        public bool CanEnrich(DeviceInfo device) => false;
        public Task<DeviceInfo> EnrichAsync(DeviceInfo device, CancellationToken ct)
            => Task.FromResult(device);
    }

    private sealed class FakeEnricher : IDeviceEnricher
    {
        public bool CanEnrich(DeviceInfo device) => false;
        public Task<DeviceInfo> EnrichAsync(DeviceInfo device, CancellationToken ct)
            => Task.FromResult(device);
    }
}

/// <summary>
/// Tests that mutate the process-wide <see cref="DeviceEnrichers"/> registry
/// share this collection so they run sequentially. xUnit runs different
/// classes in parallel by default; two test classes calling Register
/// concurrently would race the underlying ImmutableArray swap.
/// </summary>
[CollectionDefinition(nameof(DeviceEnrichersTestCollection))]
public sealed class DeviceEnrichersTestCollection { }

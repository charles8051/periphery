namespace Periphery.Tests;

/// <summary>
/// Behaviour pinning for the cross-platform <see cref="EnrichmentPipeline"/>
/// (ADR-0051 §5) — promoted from the former Windows-only pipeline so registered
/// enrichers fire on every platform. Registry-mutating, so serialised on the
/// shared <see cref="DeviceEnrichersTestCollection"/>.
/// </summary>
[Collection(nameof(DeviceEnrichersTestCollection))]
public class EnrichmentPipelineTests
{
    private static DeviceInfo Device() => new() { Id = "d", Category = DeviceCategory.Hid };

    [Fact]
    public void RunRegisteredSync_RunsMatchingEnricher()
    {
        var e = new TagAdder("Sync.X");
        try
        {
            DeviceEnrichers.Register(e);
            var result = EnrichmentPipeline.RunRegisteredSync(Device(), CancellationToken.None);
            Assert.Contains("Sync.X", result.Tags);
        }
        finally { DeviceEnrichers.Unregister(e); }
    }

    [Fact]
    public void RunRegisteredSync_SkipsWhenCanEnrichFalse()
    {
        var e = new TagAdder("Sync.Skip", canEnrich: false);
        try
        {
            DeviceEnrichers.Register(e);
            var result = EnrichmentPipeline.RunRegisteredSync(Device(), CancellationToken.None);
            Assert.DoesNotContain("Sync.Skip", result.Tags);
        }
        finally { DeviceEnrichers.Unregister(e); }
    }

    [Fact]
    public void RunRegisteredSync_SwallowsEnricherException_DevicePassesThrough()
    {
        var boom = new Thrower();
        try
        {
            DeviceEnrichers.Register(boom);
            var device = Device();
            var result = EnrichmentPipeline.RunRegisteredSync(device, CancellationToken.None);
            Assert.Same(device, result); // unchanged, no throw
        }
        finally { DeviceEnrichers.Unregister(boom); }
    }

    [Fact]
    public async Task RunRegisteredAsync_RunsMatchingEnricher()
    {
        var e = new TagAdder("Async.Y");
        try
        {
            DeviceEnrichers.Register(e);
            var result = await EnrichmentPipeline.RunRegisteredAsync(Device(), CancellationToken.None);
            Assert.Contains("Async.Y", result.Tags);
        }
        finally { DeviceEnrichers.Unregister(e); }
    }

    [Fact]
    public async Task RunRegisteredAsync_CancellationPropagates()
    {
        var e = new Canceller();
        try
        {
            DeviceEnrichers.Register(e);
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                EnrichmentPipeline.RunRegisteredAsync(Device(), new CancellationToken(canceled: true)));
        }
        finally { DeviceEnrichers.Unregister(e); }
    }

    private sealed class TagAdder(string tag, bool canEnrich = true) : IDeviceEnricher
    {
        public bool CanEnrich(DeviceInfo device) => canEnrich;
        public Task<DeviceInfo> EnrichAsync(DeviceInfo device, CancellationToken ct)
            => Task.FromResult(device with { Tags = device.Tags.Add(tag) });
    }

    private sealed class Thrower : IDeviceEnricher
    {
        public bool CanEnrich(DeviceInfo device) => true;
        public Task<DeviceInfo> EnrichAsync(DeviceInfo device, CancellationToken ct)
            => throw new InvalidOperationException("boom");
    }

    private sealed class Canceller : IDeviceEnricher
    {
        public bool CanEnrich(DeviceInfo device) => true;
        public Task<DeviceInfo> EnrichAsync(DeviceInfo device, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(device);
        }
    }
}

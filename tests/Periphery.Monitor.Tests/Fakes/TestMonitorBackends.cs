using System.Collections.Generic;

namespace Periphery.Monitor.Tests.Fakes;

/// <summary>In-memory VCP backend: a dictionary of feature values plus call counters.</summary>
internal sealed class TestMonitorBackend : IMonitorBackend
{
    public Dictionary<byte, VcpFeatureValue> Features { get; } = new()
    {
        [VcpCode.Luminance] = new VcpFeatureValue(Current: 50, Maximum: 100),
        [VcpCode.PowerMode] = new VcpFeatureValue(Current: 0x01, Maximum: 0x05),
    };

    public string CapabilitiesString { get; set; } = "(model(Fake)vcp(10 12 D6(01 04))mccs_ver(2.2))";
    public int CapabilitiesReads { get; private set; }
    public List<(byte Code, ushort Value)> Writes { get; } = [];
    public bool Disposed { get; private set; }

    public Task<VcpFeatureValue> GetVcpFeatureAsync(byte code, CancellationToken ct) =>
        Features.TryGetValue(code, out var value)
            ? Task.FromResult(value)
            : throw new MonitorTransferException($"fake: VCP 0x{code:X2} unsupported");

    public Task SetVcpFeatureAsync(byte code, ushort value, CancellationToken ct)
    {
        Writes.Add((code, value));
        if (Features.TryGetValue(code, out var existing))
            Features[code] = existing with { Current = value };
        return Task.CompletedTask;
    }

    public Task<string> GetCapabilitiesStringAsync(CancellationToken ct)
    {
        CapabilitiesReads++;
        return Task.FromResult(CapabilitiesString);
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

/// <summary>In-memory display-mode backend over a mutable current mode.</summary>
internal sealed class TestDisplayModeBackend : IDisplayModeBackend
{
    public DisplayMode CurrentMode { get; set; } = new(1920, 1080, 60);
    public MonitorOrientation Orientation { get; set; } = MonitorOrientation.Landscape;
    public List<DisplayMode> SupportedModes { get; } =
        [new(1920, 1080, 60), new(1280, 720, 60), new(720, 1280, 60)];
    public bool? LastPersist { get; private set; }
    public bool Disposed { get; private set; }

    public Task<DisplayMode> GetCurrentModeAsync(CancellationToken ct) => Task.FromResult(CurrentMode);

    public Task<IReadOnlyList<DisplayMode>> GetSupportedModesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<DisplayMode>>(SupportedModes);

    public Task SetModeAsync(DisplayMode mode, bool persist, CancellationToken ct)
    {
        CurrentMode = mode;
        LastPersist = persist;
        return Task.CompletedTask;
    }

    public Task<MonitorOrientation> GetOrientationAsync(CancellationToken ct) =>
        Task.FromResult(Orientation);

    public Task SetOrientationAsync(MonitorOrientation orientation, bool persist, CancellationToken ct)
    {
        Orientation = orientation;
        LastPersist = persist;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

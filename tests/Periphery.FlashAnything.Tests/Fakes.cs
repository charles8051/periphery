using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Periphery.FlashAnything.Tests;

/// <summary>A bootloader provider whose matching and programmer are supplied by the test.</summary>
internal sealed class FakeBootloaderProvider(
    string name,
    Func<DeviceInfo, bool> canHandle,
    Func<DeviceInfo, IFirmwareProgrammer> open,
    IdentificationMode identification = IdentificationMode.Passive) : IBootloaderProvider
{
    public string Name { get; } = name;
    public IdentificationMode Identification { get; } = identification;
    public bool CanHandle(DeviceInfo device) => canHandle(device);
    public Task<IFirmwareProgrammer> OpenAsync(DeviceInfo device, CancellationToken ct = default) =>
        Task.FromResult(open(device));
}

/// <summary>A programmer that returns canned results and records which calls were made.</summary>
internal sealed class FakeFirmwareProgrammer(
    DeviceInfo device,
    FlashResult? result = null,
    DeviceIdentity? identity = null,
    bool throwOnFlash = false,
    ImmutableArray<FirmwareFormat>? acceptedFormats = null,
    Action? onFlashed = null) : IFirmwareProgrammer
{
    private readonly FlashResult _result = result ?? FlashResult.Ok(0, verified: true);
    private readonly DeviceIdentity _identity = identity ?? DeviceIdentity.Unknown("Fake");

    public DeviceInfo Device { get; } = device;
    public bool IdentifyCalled { get; private set; }
    public bool LeaveCalled { get; private set; }

    /// <summary>The payload the last <see cref="FlashAsync"/> received (for asserting conversion).</summary>
    public FirmwarePayload? FlashedPayload { get; private set; }

    public ImmutableArray<FirmwareFormat> AcceptedFormats { get; } =
        acceptedFormats ?? ImmutableArray.Create(FirmwareFormat.IntelHex, FirmwareFormat.RawBinary, FirmwareFormat.Elf);

    public Task<DeviceIdentity> IdentifyAsync(CancellationToken ct = default)
    {
        IdentifyCalled = true;
        return Task.FromResult(_identity);
    }

    public Task<FlashResult> FlashAsync(
        FirmwarePayload payload, FlashOptions options, IProgress<FlashProgress>? progress = null, CancellationToken ct = default)
    {
        FlashedPayload = payload;
        if (throwOnFlash) throw new BootloaderException("fake transport wedged");
        progress?.Report(new FlashProgress(FlashPhase.Writing, payload.ByteLength / 2, payload.ByteLength));
        progress?.Report(new FlashProgress(FlashPhase.Writing, payload.ByteLength, payload.ByteLength));
        // Contract: FlashAsync leaves the bootloader itself when asked (the real programmer does
        // it as the plan's final step), so callers must not leave again afterwards.
        if (options.LeaveAfterFlash && _result.Success)
            LeaveCalled = true;
        // Lets a test simulate the device resetting back into application mode as a side effect of
        // a successful flash (e.g. re-plugging the app device on a FakeMonitor) - the real EFM8
        // boot-record stream's trailing RunApp record does exactly this.
        onFlashed?.Invoke();
        return Task.FromResult(_result);
    }

    public Task LeaveAsync(CancellationToken ct = default)
    {
        LeaveCalled = true;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>An app-mode bootloader entry whose matching, expected-bootloader filter, and reboot are supplied by the test.</summary>
internal sealed class FakeBootloaderEntry(
    string name,
    Func<DeviceInfo, bool> canEnter,
    DeviceFilter expectedBootloader,
    Func<DeviceInfo, Task>? onEnter = null,
    bool canVerify = false,
    Func<DeviceInfo, FirmwarePayload, Task<bool>>? verify = null) : IBootloaderEntry
{
    public string Name { get; } = name;
    public bool CanEnter(DeviceInfo applicationDevice) => canEnter(applicationDevice);
    public DeviceFilter ExpectedBootloader { get; } = expectedBootloader;
    public Task EnterAsync(DeviceInfo applicationDevice, CancellationToken ct) =>
        onEnter?.Invoke(applicationDevice) ?? Task.CompletedTask;

    /// <inheritdoc/>
    public bool CanVerify { get; } = canVerify;

    /// <inheritdoc/>
    public Task<bool> VerifyAsync(DeviceInfo bootloaderDevice, FirmwarePayload payload, CancellationToken ct) =>
        verify?.Invoke(bootloaderDevice, payload) ?? Task.FromResult(true);
}

/// <summary>A firmware converter whose source/target formats and transform are supplied by the test.</summary>
internal sealed class FakeFirmwareConverter(
    FirmwareFormat source, FirmwareFormat target, Func<ReadOnlyMemory<byte>, FirmwarePayload> convert) : IFirmwareConverter
{
    public FirmwareFormat Source { get; } = source;
    public FirmwareFormat Target { get; } = target;
    public FirmwarePayload Convert(ReadOnlyMemory<byte> sourceContent) => convert(sourceContent);
}

internal static class FakeDevices
{
    /// <summary>A minimal discoverable device (only <see cref="DeviceInfo.Id"/> is required).</summary>
    public static DeviceInfo Usb(string id, string? name = null) => new() { Id = id, Name = name ?? id };

    /// <summary>
    /// A device whose driver has started: what a Start-time snapshot reports for a device that
    /// can be opened. <see cref="Usb"/> leaves <see cref="DeviceInfo.IsActive"/> false, which is
    /// a device that is present but not started, and which autoflash must not open (ADR-0088).
    /// </summary>
    public static DeviceInfo ActiveUsb(string id, string? name = null) => Usb(id, name) with { IsActive = true };

    /// <summary>
    /// A <see cref="DeviceWatcher"/> over fake providers: <paramref name="snapshot"/> is the
    /// Start-time device set; <paramref name="monitor"/> drives live plug/unplug.
    /// </summary>
    public static DeviceWatcher Watcher(FakeMonitor monitor, params DeviceInfo[] snapshot)
        => new(new FakeDeviceProvider(snapshot), monitor);
}

/// <summary>An <see cref="IDeviceProvider"/> over a fixed device set (the Start snapshot).</summary>
internal sealed class FakeDeviceProvider(params DeviceInfo[] devices) : IDeviceProvider
{
    public async IAsyncEnumerable<DeviceInfo> EnumerateAsync(
        DeviceFilter filter, [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var d in devices)
        {
            await Task.Yield();
            yield return d;
        }
    }
}

/// <summary>An <see cref="IDeviceMonitorProvider"/> the test drives to simulate plug/unplug.</summary>
#pragma warning disable CS0067 // not every interface event is exercised by the tests
internal sealed class FakeMonitor : IDeviceMonitorProvider
{
    public Task StartAsync(DeviceFilter filter, CancellationToken ct = default) => Task.CompletedTask;
    public event EventHandler<DeviceChangeEventArgs>? DeviceAppeared;
    public event EventHandler<DeviceChangeEventArgs>? DeviceDisappeared;
    public event EventHandler<DeviceChangeEventArgs>? DeviceActivated;
    public event EventHandler<DeviceChangeEventArgs>? DeviceDeactivated;
    public event EventHandler<DeviceModificationEventArgs>? DevicePropertyChanged;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// Simulate a device plugging in (arrival + activation).
    /// <para>The activation payload is stamped <see cref="DeviceInfo.IsActive"/> because that is
    /// what every real provider does and what the flag means: Windows builds it from a fresh
    /// <c>DN_STARTED</c> read, Linux from <c>authorized</c>, macOS from the session property. A
    /// fake that raised <c>DeviceActivated</c> carrying <c>IsActive == false</c> put the tracker in
    /// a state hardware cannot produce — the connected latch claimed while
    /// <c>DeviceTrackerResolution.Resolve</c> reads the snapshot flag and reports only Present. The
    /// fixtures here do not set the flag, so every activation was landing that way, and consumers
    /// asserting on readiness were being handed a device that on real hardware could not be
    /// opened.</para>
    /// </summary>
    public void Plug(DeviceInfo device)
    {
        Appear(device);
        Activate(device);
    }

    /// <summary>
    /// Simulate arrival only: the devnode is in the tree and its driver has not started. On
    /// Windows this is <c>DEVICEINSTANCEENUMERATED</c>; the payload says <c>IsActive == false</c>
    /// because that is what a status read returns before <c>DN_STARTED</c>.
    /// </summary>
    public void Appear(DeviceInfo device) =>
        DeviceAppeared?.Invoke(this, new DeviceChangeEventArgs(device with { IsActive = false }));

    /// <summary>Simulate the driver starting for a device that has already appeared.</summary>
    public void Activate(DeviceInfo device) =>
        DeviceActivated?.Invoke(this, new DeviceChangeEventArgs(device with { IsActive = true }));

    /// <summary>
    /// Simulate a device unplugging (deactivation + disappearance) - symmetric with
    /// <see cref="Plug"/>'s "arrival + activation," so a device that unplugs and later re-plugs is
    /// tracked as a fresh appearance. MultiDeviceTracker.OnDeviceActivated/OnDeviceDeactivated drive
    /// each child's connected/disconnected state independently of Appeared/Disappeared - firing only
    /// Disappeared here left a re-plugged device's activity state unchanged from its still-connected
    /// prior appearance, so a second wait for it (e.g. re-entering a bootloader a second time) never
    /// observed a fresh activation.
    /// </summary>
    public void Unplug(DeviceInfo device)
    {
        Deactivate(device);
        DeviceDisappeared?.Invoke(this, new DeviceChangeEventArgs(device));
    }

    /// <summary>
    /// Simulate the driver stopping while the devnode stays in the tree: <c>Deactivated</c> with no
    /// <c>Disappeared</c>. On Windows this has no soft edge, but a bench device that resets its own
    /// link produces it, and it is the case that leaves a present-but-inactive target.
    /// </summary>
    public void Deactivate(DeviceInfo device) =>
        DeviceDeactivated?.Invoke(this, new DeviceChangeEventArgs(device with { IsActive = false }));
}
#pragma warning restore CS0067

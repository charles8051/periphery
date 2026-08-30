namespace Periphery.Tests;

/// <summary>
/// Abstract contract test suite for <see cref="IDeviceMonitorProvider"/>.
/// Subclass and implement <see cref="CreateMonitorCore"/> to verify a concrete monitor provider.
/// </summary>
/// <remarks>
/// Contract rules under test:
/// <list type="number">
///   <item><see cref="IDeviceMonitorProvider.StartAsync"/> succeeds on the first call.</item>
///   <item>A second <see cref="IDeviceMonitorProvider.StartAsync"/> throws
///       <see cref="InvalidOperationException"/> — double-start is a programming error.</item>
///   <item><see cref="IAsyncDisposable.DisposeAsync"/> succeeds whether or not the monitor was started.</item>
///   <item><see cref="IAsyncDisposable.DisposeAsync"/> is idempotent — repeated calls must not throw.</item>
///   <item>Event subscription and unsubscription before and after start must not throw.</item>
/// </list>
/// <para>
/// To test a new provider (Linux, macOS, etc.) subclass this and implement
/// <see cref="CreateMonitorCore"/>. All lifecycle assertions are inherited automatically.
/// </para>
/// </remarks>
public abstract class DeviceMonitorProviderContractTests
{
    /// <summary>
    /// Create a fresh, unstarted monitor provider.
    /// Return it boxed as <see cref="object"/>; it must implement
    /// <see cref="IDeviceMonitorProvider"/> and will be cast internally.
    /// </summary>
    protected abstract object CreateMonitorCore();

    private IDeviceMonitorProvider NewMonitor() => (IDeviceMonitorProvider)CreateMonitorCore();

    // ── StartAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task StartAsync_FirstCall_DoesNotThrow()
    {
        await using var monitor = NewMonitor();

        await monitor.StartAsync(new DeviceFilter());
    }

    [Fact]
    public async Task StartAsync_CalledTwice_ThrowsInvalidOperationException()
    {
        await using var monitor = NewMonitor();
        await monitor.StartAsync(new DeviceFilter());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => monitor.StartAsync(new DeviceFilter()));
    }

    // ── DisposeAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task DisposeAsync_WithoutStarting_DoesNotThrow()
    {
        var monitor = NewMonitor();

        await monitor.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_AfterStart_DoesNotThrow()
    {
        var monitor = NewMonitor();
        await monitor.StartAsync(new DeviceFilter());

        await monitor.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_CalledMultipleTimes_IsIdempotent()
    {
        var monitor = NewMonitor();
        await monitor.StartAsync(new DeviceFilter());

        for (int i = 0; i < 5; i++)
            await monitor.DisposeAsync();
    }

    // ── Event surface ──────────────────────────────────────────────────

    [Fact]
    public async Task EventSubscription_SubscribeAndUnsubscribe_DoesNotThrow()
    {
        await using var monitor = NewMonitor();

        EventHandler<DeviceChangeEventArgs>?      h1 = (_, _) => { };
        EventHandler<DeviceModificationEventArgs>? h2 = (_, _) => { };

        monitor.DeviceAppeared       += h1;
        monitor.DeviceDisappeared    += h1;
        monitor.DeviceActivated      += h1;
        monitor.DeviceDeactivated    += h1;
        monitor.DevicePropertyChanged += h2;

        monitor.DeviceAppeared       -= h1;
        monitor.DeviceDisappeared    -= h1;
        monitor.DeviceActivated      -= h1;
        monitor.DeviceDeactivated    -= h1;
        monitor.DevicePropertyChanged -= h2;
    }
}

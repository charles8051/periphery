// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Ble.InTheHand;

/// <summary>
/// Reconnect-resilient GATT session for a bonded Bluetooth LE peripheral (ADR-0085 D7, issue #314).
/// While the peripheral is present it keeps a <see cref="BleSession"/> connected, and it connects
/// again after the link drops.
/// </summary>
/// <remarks>
/// <para><b>It opens on presence, not activity.</b> A peripheral that is not a HID device has no
/// link until a central connects, so waiting for the link node to go active would wait forever
/// (ADR-0088 amendment). While the peripheral is out of range each attempt fails with a
/// <see cref="BleException"/>, raised as <c>OpenFailed</c>, and the recovery policy spaces the next.
/// On Windows an attempt at a silent peripheral took about 23 s to fail.</para>
/// <para><b>Bind by the link node.</b> A <see cref="DeviceProfile"/> selecting
/// <c>WithBluetoothTransport(BluetoothTransport.LowEnergy)</c> and <c>WithMacAddress</c> matches the
/// <c>BTHLE\DEV_</c> node on Windows and the BlueZ bond on Linux. The address is the key, so a
/// private-address peripheral that is re-paired comes back under a new node on Windows, and a
/// profile bound to the old one stays absent (issue #232, ADR-0083).</para>
/// <para>It never resets the device: Bluetooth devices get a <see cref="NullDeviceReset"/>.</para>
/// </remarks>
public sealed class BleDeviceProxy : DeviceProxyBase<BleSession, BleException>
{
    private BleDeviceProxy(DeviceTracker tracker, DeviceWatcher watcher, IRecoveryPolicy? recoveryPolicy)
        : base(tracker, watcher, recoveryPolicy, NullDeviceReset.Instance) { }

    private BleDeviceProxy(DeviceTracker tracker, IRecoveryPolicy? recoveryPolicy)
        : base(tracker, recoveryPolicy, NullDeviceReset.Instance) { }

    /// <inheritdoc />
    protected override bool OpensWhilePresent => true;

    /// <inheritdoc />
    protected override bool HasWorker => true;

    /// <inheritdoc />
    protected override async Task<BleSession> OpenDeviceAsync(DeviceInfo deviceInfo, CancellationToken ct)
    {
        global::InTheHand.Bluetooth.BluetoothDevice? device;
        try
        {
            device = await deviceInfo.ToBluetoothDeviceAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new BleException($"'{deviceInfo.Id}' did not resolve to a Bluetooth LE device.", ex);
        }

        if (device is null)
            throw new BleException($"'{deviceInfo.Id}' did not resolve to a Bluetooth LE device.");

        // Subscribed before connecting, so a drop during the connect is not missed.
        var session = new BleSession(device);
        var connect = device.Gatt.ConnectAsync();
        try
        {
            await connect.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw new BleException($"'{deviceInfo.Id}' did not connect.", ex);
        }
        catch
        {
            // ConnectAsync takes no token and runs on. Disconnect again once it settles, so a
            // connection that completes after the cancel is not left up.
            await session.DisposeAsync().ConfigureAwait(false);
            _ = DisconnectWhenSettledAsync(connect, session);
            throw;
        }

        // On Windows ConnectAsync returns without throwing when the peripheral is silent.
        if (!device.Gatt.IsConnected)
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw new BleException($"'{deviceInfo.Id}' did not connect; it may be out of range.");
        }
        return session;
    }

    private static async Task DisconnectWhenSettledAsync(Task connect, BleSession session)
    {
        try
        {
            await connect.ConfigureAwait(false);
        }
        catch
        {
            // A connect that failed left nothing to disconnect.
        }
        session.Gatt.Disconnect();
    }

    /// <summary>Fails when the link drops, which closes the session and connects again.</summary>
    protected override Task WhileOpenAsync(BleSession device, CancellationToken ct) =>
        device.WaitForLinkDropAsync(ct);

    /// <summary>
    /// Creates a self-contained proxy that owns its watcher and connects to the peripheral
    /// matching <paramref name="profile"/> whenever it is present.
    /// </summary>
    public static Task<BleDeviceProxy> OpenAsync(
        DeviceProfile profile,
        IRecoveryPolicy? recoveryPolicy = null,
        CancellationToken ct = default)
        => OpenWithOwnedWatcherAsync(
            profile,
            (tracker, watcher) => new BleDeviceProxy(tracker, watcher, recoveryPolicy),
            ct);

    /// <summary>
    /// Creates a proxy that borrows a caller-owned <paramref name="tracker"/> already attached to a
    /// running watcher.
    /// </summary>
    public static BleDeviceProxy Create(
        DeviceTracker tracker,
        IRecoveryPolicy? recoveryPolicy = null)
        => CreateWithBorrowedTracker(
            tracker,
            t => new BleDeviceProxy(t, recoveryPolicy));
}

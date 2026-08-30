// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Periphery.Camera.Internal;

namespace Periphery.Camera.Testing;

/// <summary>
/// Redirects <see cref="CameraDevice"/>'s backend construction to an
/// <see cref="InMemoryCameraBackend"/> for the lifetime of the scope, then
/// restores the previous behaviour on <see cref="Dispose"/>.
/// </summary>
/// <remarks>
/// <para>
/// Use this when the code under test opens a camera from a <see cref="DeviceInfo"/>
/// itself — e.g. <c>CameraSession.For(deviceInfo).OpenAsync()</c> or
/// <c>CameraDevice.OpenAsync(deviceInfo)</c> — so there is no seam to hand a
/// backend to directly. Inside the scope those calls resolve to the installed
/// fake instead of real hardware:
/// </para>
/// <code>
/// var backend = new InMemoryCameraBackend();
/// using (CameraTestScope.Install(backend))
/// {
///     await using var session = await CameraSession
///         .For(CameraTestFormats.CreateDeviceInfo())
///         .PreferYuy2()
///         .OpenAsync();
///     await foreach (var frame in session.CaptureAsync(ct: ct)) { /* assert */ }
/// }
/// </code>
/// <para>
/// <b>The redirect is process-global.</b> Overlapping scopes (from tests running
/// in parallel) clobber each other, so keep camera tests that install a scope in
/// a single, non-parallel test collection — the same constraint Periphery's own
/// camera suite honours. When the code under test accepts an already-open
/// <see cref="CameraSession"/>, prefer <see cref="CameraTestHarness"/>, which
/// touches no global state.
/// </para>
/// </remarks>
public sealed class CameraTestScope : IDisposable
{
    private readonly Func<DeviceInfo, ICameraBackend>? _previous;
    private readonly Func<DeviceInfo, ICameraBackend> _installed;
    private bool _disposed;

    private CameraTestScope(Func<DeviceInfo, ICameraBackend> factory)
    {
        _previous = CameraDevice.BackendFactory;
        _installed = factory;
        CameraDevice.BackendFactory = factory;
    }

    /// <summary>
    /// Install <paramref name="backend"/> as the backend for <em>every</em> device
    /// opened while the scope is active.
    /// </summary>
    /// <remarks>
    /// <b>Single-open only.</b> An <see cref="InMemoryCameraBackend"/> models one
    /// device lifecycle, so this overload fits code that opens a device exactly
    /// once — e.g. <c>CameraDevice.OpenAsync(deviceInfo)</c> or
    /// <c>CameraDevice.ReadSnapshotAsync(deviceInfo)</c>. The
    /// <see cref="CameraSessionBuilder"/> path
    /// (<c>CameraSession.For(deviceInfo).OpenAsync()</c>) opens the device
    /// <em>twice</em> — a snapshot pass that reads formats and disposes, then the
    /// capture open — so a single shared instance is already disposed by the
    /// second open and throws <see cref="ObjectDisposedException"/>. For
    /// builder-based code use the
    /// <see cref="Install(Func{DeviceInfo, InMemoryCameraBackend})"/> overload (a
    /// fresh backend per open), or drive the code with
    /// <see cref="CameraTestHarness"/>, which opens once.
    /// </remarks>
    public static CameraTestScope Install(InMemoryCameraBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        return new CameraTestScope(_ => backend);
    }

    /// <summary>
    /// Install a factory that produces a backend per opened <see cref="DeviceInfo"/>
    /// — for multi-device tests where each device should resolve to its own fake.
    /// </summary>
    public static CameraTestScope Install(Func<DeviceInfo, InMemoryCameraBackend> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return new CameraTestScope(device => factory(device));
    }

    /// <summary>
    /// Restore the backend-construction behaviour in effect when the scope was
    /// installed — but only if this scope still owns the current factory. If a
    /// newer scope was installed and not yet disposed (out-of-order disposal),
    /// restoring here would clobber it, so this becomes a no-op.
    /// </summary>
    /// <remarks>
    /// <b>Out-of-order disposal does not fully unwind.</b> The no-op above leaves
    /// the newer scope holding a <c>_previous</c> that points at <em>this</em>
    /// (now-disposed) scope's factory, so when the newer scope is disposed it
    /// restores that defunct factory process-globally rather than the pre-scope
    /// state — subsequent opens resolve to a fake nobody owns. There is no better
    /// answer with a single global slot: dispose scopes in LIFO order (a
    /// <c>using</c> block or declaration does this for you), and keep camera tests
    /// that install a scope in one non-parallel collection.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (ReferenceEquals(CameraDevice.BackendFactory, _installed))
            CameraDevice.BackendFactory = _previous;
    }
}

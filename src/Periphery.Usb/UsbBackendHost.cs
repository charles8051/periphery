// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Periphery.Usb;

/// <summary>
/// Backend factories that a separate package installs, for platforms <see cref="UsbDevice"/> has no
/// built-in backend for (ADR-0092 D6). The shape follows <c>CameraDevice.BackendFactory</c>
/// (ADR-0065).
/// </summary>
internal static class UsbBackendHost
{
    /// <summary>
    /// Opens a device in a browser. <c>Periphery.Usb.Browser</c> sets it from
    /// <c>BrowserUsb.InitializeAsync</c>. <see cref="UsbDevice.OpenAsync(DeviceInfo, CancellationToken)"/>
    /// consults it only when <see cref="OperatingSystem.IsBrowser"/> is true.
    /// </summary>
    internal static Func<DeviceInfo, CancellationToken, Task<IUsbBackend>>? Browser { get; set; }
}

---
title: "ADR-0010: Linux Provider via libudev + netlink"
status: "Accepted"
date: "2026-03-10"
authors: "@charles8051 (proposal)"
tags: ["architecture", "decision"]
supersedes: ""
superseded_by: ""
---

# ADR-0010: Linux Provider via libudev + netlink

**Tracks:** Linux platform provider implementation  
**Supersedes:** (none — implements the Linux provider sketched in ARCHITECTURE.md §2.3)

---

## Context

`ARCHITECTURE.md §2.3` marks the Linux provider as "planned" and describes its shape: sysfs
walking or `libudev` for enumeration, and a `netlink` socket for monitoring. This ADR formalises
that choice, evaluates the concrete alternatives, and documents the implementation contract that
`LinuxDeviceProvider` and `LinuxDeviceMonitorProvider` must fulfil.

The Linux device model is exposed through three overlapping surfaces:

**1. The sysfs virtual filesystem (`/sys`)**  
Every device visible to the kernel appears as a directory tree rooted at `/sys/devices`. Symlink
forests under `/sys/bus/<subsystem>/devices` and `/sys/class/<subsystem>` provide subsystem-scoped
views. Properties are plain text files. Walking sysfs requires no native library and no elevated
privileges for most device attributes, but reading the tree correctly requires knowledge of kernel
kobject rules that are underdocumented and have changed between kernel versions.

**2. `libudev` (`udev_enumerate_*` / `udev_monitor_*`)**  
`libudev` is the stable, ABI-versioned C library that `udev` (and its successor `systemd-udev`)
exposes for enumerating and monitoring the device database. It provides a well-defined, kernel-
version-independent API on top of the same sysfs data, adds the device database (udev rules
resolution), and handles the kernel↔userspace uevent filtering. It is available on every modern
mainstream Linux distribution that ships `systemd`.

**3. Raw `netlink` socket (`AF_NETLINK`, `NETLINK_KOBJECT_UEVENT`)**  
The kernel broadcasts device add/remove/change events directly over a netlink socket, bypassing
libudev. This is the most primitive monitoring mechanism and gives the lowest latency. However it
requires parsing raw uevent text payloads and re-implementing filtering that libudev handles
transparently.

The question is which surface to build the provider on — and for enumeration and monitoring
separately, since the optimal answer differs.

---

## Decision Drivers

| Driver | Raw sysfs | libudev P/Invoke | Raw netlink |
|---|---|---|---|
| AOT / trim safe | ✅ pure managed I/O | ✅ `[LibraryImport]`, no reflection | ✅ pure managed socket I/O |
| No runtime service dependency | ✅ kernel VFS always present | 🟡 `libudev.so.1` must be installed | ✅ kernel always present |
| Event latency | n/a (polling only) | ✅ synchronous kernel notification | ✅ synchronous kernel notification |
| Property coverage / correctness | 🟡 raw sysfs, udev rules not applied | ✅ full udev database, rules applied | ❌ raw uevent payload only |
| Portability across distros | 🟡 sysfs paths vary by kernel/distro | ✅ stable libudev ABI | 🟡 uevent format stable, filtering not |
| Implementation complexity | 🟡 moderate (path walking + parsing) | 🟡 moderate (interop structs + P/Invoke) | ❌ high (full uevent parser + filter) |
| Monitoring support | ❌ polling required | ✅ `udev_monitor_receive_device` | ✅ socket `recv` |

---

## Options Considered

### Option A — Walk sysfs directly

Enumerate `/sys/bus/<subsystem>/devices` and `/sys/class/<subsystem>` directories, reading
property files (`uevent`, `idVendor`, `idProduct`, `manufacturer`, etc.) directly from the VFS.
Monitor changes by polling these paths or by subscribing to a raw netlink socket.

**Rejected for enumeration.** sysfs paths are not stable guarantees — they reflect kernel internal
kobject topology, which has changed across major kernel versions. The `uevent` files only contain
the minimal set of properties the kernel chooses to export; udev rules that rename devices, assign
symlinks, or add synthesised attributes (e.g. `ID_MODEL_FROM_DATABASE`) are never visible. The
resulting `DeviceInfo` would be incomplete and subtly incorrect on hardened or non-standard distros.

---

### Option B — libudev via `[LibraryImport]` ✅ Recommended for enumeration

P/Invoke into `libudev.so.1` using `[LibraryImport]` declarations. `libudev` abstracts away sysfs
path instability, applies all active udev rules, and exposes a typed enumeration API.

The `udev_enumerate` family covers snapshot queries:

```
udev_new()
  → udev_enumerate_new()
    → udev_enumerate_add_match_subsystem("usb")
    → udev_enumerate_scan_devices()
    → udev_enumerate_get_list_entry() … udev_list_entry_get_next()
      → udev_device_new_from_syspath()
        → udev_device_get_property_value("ID_MODEL")
        → udev_device_get_sysattr_value("idVendor")
```

The `udev_monitor` family covers real-time events:

```
udev_monitor_new_from_netlink(udev, "udev")  ← rule-processed events
  → udev_monitor_filter_add_match_subsystem_devtype("usb", NULL)
  → udev_monitor_enable_receiving()
  → udev_monitor_get_fd() → poll/epoll on file descriptor
    → udev_monitor_receive_device()
      → action = "add" / "remove" / "change"
```

Subscribing to the `"udev"` source (rather than `"kernel"`) means the events have already been
processed through udev rules — device names, symlinks, and synthesised attributes are fully
resolved when the callback fires.

**`libudev.so.1` availability:** On any systemd-based distro (Ubuntu, Fedora, Debian, Arch, RHEL,
openSUSE, etc.) `libudev` is part of the base install. On non-systemd distros (`eudev` on Alpine,
Gentoo with OpenRC), `eudev` ships a compatible `libudev.so.1`. The only environments where
`libudev` is absent are minimal container base images (e.g. `mcr.microsoft.com/dotnet/runtime`
Alpine variant) — the provider must detect this and throw `DeviceProviderException` with a
diagnostic message.

---

### Option C — Raw netlink socket

Open `AF_NETLINK` / `NETLINK_KOBJECT_UEVENT` directly via `System.Net.Sockets` and parse the
raw kernel uevent payloads (null-byte-separated `KEY=VALUE` strings).

**Adopted for monitoring fallback only.** When `libudev.so.1` is not available, the monitor
provider can fall back to a raw netlink socket. Raw netlink gives synchronous kernel notification
with no library dependency. The trade-off is that rule-processed attributes are not available —
only the kernel-level properties survive. This is acceptable for the monitor path (connect/
disconnect detection), though the resulting `DeviceInfo` snapshots are shallower than those from
the libudev enumeration path.

**Not adopted for enumeration.** Netlink is push-only; it cannot enumerate currently attached
devices. The sysfs walk shortcomings (Option A) apply if netlink is used as the sole data source.

---

### Option D — `udevadm` subprocess

Shell out to `udevadm info --query=all --name=<device>` or `udevadm monitor`.

**Rejected.** Subprocess overhead is unacceptable for enumeration of dozens of devices. The
subprocess model is fragile (PATH, locale, `udevadm` binary version drift), not AOT-safe (requires
`Process`), and not suitable for a production library.

---

## Decision

**Enumerate with libudev (Option B). Monitor with libudev where available, falling back to raw
netlink (Option C) when `libudev.so.1` is absent.**

All P/Invoke declarations use `[LibraryImport]` (not `[DllImport]`). The provider is guarded with
`[SupportedOSPlatform("linux")]` and resolved only when `OperatingSystem.IsLinux()` is true.

A `UdevInterop.cs` file centralises all `[LibraryImport]` declarations for `libudev`, following
the same pattern as `DevNodeHelper.cs` on Windows.

---

## Implementation Sketch

### Enumeration

```csharp
// UdevInterop.cs (partial, [LibraryImport] declarations)
[LibraryImport("libudev.so.1")]
internal static partial IntPtr udev_new();

[LibraryImport("libudev.so.1")]
internal static partial IntPtr udev_enumerate_new(IntPtr udev);

[LibraryImport("libudev.so.1", StringMarshalling = StringMarshalling.Utf8)]
internal static partial int udev_enumerate_add_match_subsystem(IntPtr enumerate, string subsystem);

[LibraryImport("libudev.so.1")]
internal static partial int udev_enumerate_scan_devices(IntPtr enumerate);

[LibraryImport("libudev.so.1")]
internal static partial IntPtr udev_enumerate_get_list_entry(IntPtr enumerate);

[LibraryImport("libudev.so.1")]
internal static partial IntPtr udev_list_entry_get_next(IntPtr listEntry);

[LibraryImport("libudev.so.1", StringMarshalling = StringMarshalling.Utf8)]
[return: MarshalAs(UnmanagedType.LPUTF8Str)]
internal static partial string? udev_list_entry_get_name(IntPtr listEntry);

[LibraryImport("libudev.so.1", StringMarshalling = StringMarshalling.Utf8)]
internal static partial IntPtr udev_device_new_from_syspath(IntPtr udev, string syspath);

[LibraryImport("libudev.so.1", StringMarshalling = StringMarshalling.Utf8)]
[return: MarshalAs(UnmanagedType.LPUTF8Str)]
internal static partial string? udev_device_get_property_value(IntPtr device, string key);

[LibraryImport("libudev.so.1", StringMarshalling = StringMarshalling.Utf8)]
[return: MarshalAs(UnmanagedType.LPUTF8Str)]
internal static partial string? udev_device_get_sysattr_value(IntPtr device, string sysattr);

[LibraryImport("libudev.so.1")]
internal static partial void udev_device_unref(IntPtr device);

[LibraryImport("libudev.so.1")]
internal static partial void udev_enumerate_unref(IntPtr enumerate);

[LibraryImport("libudev.so.1")]
internal static partial void udev_unref(IntPtr udev);
```

```csharp
// LinuxDeviceProvider.EnumerateAsync() — conceptual flow
var udev = UdevInterop.udev_new();
var enumerate = UdevInterop.udev_enumerate_new(udev);

foreach (var subsystem in LinuxCategoryMap.GetSubsystems(filter.Category))
    UdevInterop.udev_enumerate_add_match_subsystem(enumerate, subsystem);

UdevInterop.udev_enumerate_scan_devices(enumerate);

var entry = UdevInterop.udev_enumerate_get_list_entry(enumerate);
while (entry != IntPtr.Zero)
{
    var syspath = UdevInterop.udev_list_entry_get_name(entry);
    var dev     = UdevInterop.udev_device_new_from_syspath(udev, syspath!);
    try
    {
        var info = ToDeviceInfo(dev, syspath!);
        if (info is not null && filter.Matches(info))
            yield return info;
    }
    finally { UdevInterop.udev_device_unref(dev); }

    entry = UdevInterop.udev_list_entry_get_next(entry);
}
```

### Change notifications (libudev monitor)

```csharp
// LinuxDeviceMonitorProvider — libudev path
var monitor = UdevInterop.udev_monitor_new_from_netlink(udev, "udev");
UdevInterop.udev_monitor_filter_add_match_subsystem_devtype(monitor, "usb", null);
UdevInterop.udev_monitor_enable_receiving(monitor);

int fd = UdevInterop.udev_monitor_get_fd(monitor);
// Poll fd on a dedicated Task (Task.Factory.StartNew with LongRunning)
// On readable: call udev_monitor_receive_device(), read action string
// "add" → fire DeviceConnected; "remove" → fire DeviceDisconnected
```

### Change notifications (netlink fallback)

```csharp
// LinuxDeviceMonitorProvider — netlink fallback path
var socket = new Socket(AddressFamily.Netlink, SocketType.Raw, (ProtocolType)15 /* NETLINK_KOBJECT_UEVENT */);
socket.Bind(new NetlinkSocketAddress(0, 1)); // pid=0 (auto), groups=1 (kernel multicast)

// Receive loop on dedicated Task:
// Parse null-byte-delimited KEY=VALUE pairs from each datagram
// Extract ACTION= ("add"/"remove"), SUBSYSTEM=, DEVPATH=, ID_VENDOR_ID=, ID_MODEL_ID=
// Build a minimal DeviceInfo; fire DeviceConnected / DeviceDisconnected
```

---

## Property Mapping

| `DeviceInfo` property | libudev source | Fallback |
|---|---|---|
| `Id` | `udev_device_get_syspath()` | `DEVPATH` uevent key |
| `Name` | `ID_MODEL` property | `PRODUCT` (USB) |
| `Manufacturer` | `ID_VENDOR` property | `MANUFACTURER` sysattr |
| `VendorId` | `ID_VENDOR_ID` property (hex 4-digit) | `idVendor` sysattr |
| `ProductId` | `ID_MODEL_ID` property (hex 4-digit) | `idProduct` sysattr |
| `SerialNumber` | `ID_SERIAL_SHORT` property | `serial` sysattr |
| `Category` | `SUBSYSTEM` → `LinuxCategoryMap` | same |
| `BusType` | `ID_BUS` property (`"usb"`, `"pci"`, etc.) | `SUBSYSTEM` |
| `IsConnected` | `authorized` sysattr == `"1"` && `connected` sysattr | action == `"add"` |
| `MacAddress` | `address` sysattr under `net` subsystem | n/a |
| `Driver` | `DRIVER` property | `DRIVER` sysattr |
| `DriverVersion` | kernel module `version` sysattr | n/a |

---

## Category Mapping

A `LinuxCategoryMap` file (parallel to `DeviceClassGuids.cs`) maps `DeviceCategory` values to
libudev subsystem strings:

| `DeviceCategory` | libudev subsystem(s) | udev property hints |
|---|---|---|
| `Usb` | `usb` | `ID_BUS=usb` |
| `Bluetooth` | `bluetooth` | `ID_BUS=bluetooth` |
| `Network` | `net` | `ID_NET_NAME_*` |
| `Display` | `drm` | `ID_TYPE=video` |
| `Hid` | `hid`, `input` | |
| `Keyboard` | `input` | `ID_INPUT_KEYBOARD=1` |
| `Mouse` | `input` | `ID_INPUT_MOUSE=1` |
| `Audio` | `sound` | `ID_TYPE=audio` |
| `Storage` | `block` | `ID_TYPE=disk` |

---

## Consequences

**Positive:**

- No NuGet package additions — `libudev` is a system library; P/Invoke is zero-dependency.
- `[LibraryImport]` keeps the provider fully AOT/trim safe.
- `udev_monitor` with the `"udev"` source delivers synchronous, rule-processed notifications —
  identical event latency to the SetupAPI approach on Windows.
- `DeviceInfo` quality on Linux is high: udev rules synthesise `ID_MODEL_FROM_DATABASE`,
  `ID_VENDOR_FROM_DATABASE`, and device-class metadata that raw sysfs never exposes.

**Negative / risks:**

- **`libudev.so.1` ABI dependency.** Must detect library absence at startup and surface a clear
  `DeviceProviderException` rather than a `DllNotFoundException`. The netlink fallback covers
  monitoring in minimal-image environments but enumeration has no fallback — callers must install
  `libudev` (or `eudev`) if they need snapshot enumeration.
- **Integer file-descriptor lifecycle.** The netlink socket and udev monitor fd must be closed
  when the monitor provider is disposed. `DisposeAsync` must poll-wake the receive loop before
  releasing native handles.
- **`IntPtr` handle leaks.** Every `udev_device_new_from_syspath` call must be paired with
  `udev_device_unref` in a `try/finally` block. A `SafeHandle` wrapper for udev objects is
  strongly recommended to enforce this at compile time.
- **Root requirement for some devices.** Reading certain sysfs attributes (USB serial numbers,
  raw HID descriptors) may require `CAP_SYS_ADMIN` or udev rule adjustments. Document this as a
  platform limitation; never silently swallow `EPERM`.
- **Non-systemd environments.** Alpine Linux with musl and `eudev` works. BusyBox-based minimal
  containers with no udev daemon provide no enumeration and no `libudev` — document this boundary
  explicitly.

---

## Amendments (2026-07-14)

The following corrections and additions were identified after benchmarking the Linux design against
the finalised Windows implementation (ADR-0009 + ADR-0012). They update the implementation
contract before coding begins.

---

### 1. Action-to-event mapping was incorrect (critical)

The original sketch mapped `"add"` → `DeviceConnected` and `"remove"` → `DeviceDisconnected`.
This is wrong per the two-level state model established in ADR-0004. The correct mapping is:

| udev action | Provider fires | Notes |
|---|---|---|
| `"add"` | `DeviceAppeared` (+ `DeviceConnected` if `authorized` sysattr == `"1"`) | Device entered the OS tree |
| `"remove"` | `DeviceDisappeared` | Watcher cascades `DeviceDisconnected` |
| `"bind"` | `DeviceConnected` | Driver claimed the device (soft connect) |
| `"unbind"` | `DeviceDisconnected` | Driver released the device (soft disconnect) |
| `"change"` | `DevicePropertyChanged` | Via `DeviceInfoDiff.Compute` — see below |

This mapping is implemented in the action-dispatch `switch` in the polling loop and produces
immediately correct behaviour for Bluetooth in/out-of-range transitions (`"bind"`/`"unbind"`)
without any polling.

---

### 2. `DevicePropertyChanged` via `"change"` action

`"change"` is the udev action delivered when a device's properties mutate without a structural
add/remove. The monitor provider must:

1. Read a fresh `DeviceInfo` snapshot from `LinuxDeviceProvider.ToDeviceInfo(dev, syspath)`.
2. Look up the previous snapshot in `_lastKnownDevices` (keyed by syspath / device ID).
3. Call `DeviceInfoDiff.Compute(previous, current)` to produce the changed-property set.
4. Fire `DevicePropertyChanged` only if the diff is non-empty.

This mirrors the Windows scan-loop diff pattern, but at native latency — no `PeriodicTimer` is
needed on Linux because udev pushes `"change"` events synchronously.

The `_lastKnownDevices` cache (`Dictionary<string, DeviceInfo>`) must be seeded in `StartAsync`
by calling `LinuxDeviceProvider.ToDeviceInfo` for each present device, guarded by `_cacheLock`:

```csharp
// In StartAsync, after udev_monitor_enable_receiving():
lock (_cacheLock)
{
    // iterate udev_enumerate to build initial snapshot
    foreach (var (syspath, dev) in UdevInterop.EnumerateDevices(udev))
    {
        var info = LinuxDeviceProvider.ToDeviceInfo(dev, syspath);
        if (info is not null) _lastKnownDevices[info.Id] = info;
    }
}
```

---

### 3. `[UnmanagedCallersOnly]` does NOT apply to Linux

The Windows and macOS monitor providers use `[UnmanagedCallersOnly]` static methods as native
callback shims because `CM_Register_Notification` (Windows) and `IOServiceAddMatchingNotification`
(macOS) accept a native function pointer that the OS calls from a kernel thread.

**Linux udev monitoring works differently.** `udev_monitor_get_fd()` returns a plain integer file
descriptor. The provider polls this fd on a dedicated `Task` (`TaskCreationOptions.LongRunning`)
and calls `udev_monitor_receive_device()` to dequeue each event. There is no native callback and
no GCHandle is needed:

```csharp
// In StartAsync — no GCHandle, no [UnmanagedCallersOnly]:
_monitorCts  = new CancellationTokenSource();
_monitorTask = Task.Factory.StartNew(
    () => MonitorLoopAsync(_monitorCts.Token),
    _monitorCts.Token,
    TaskCreationOptions.LongRunning,
    TaskScheduler.Default).Unwrap();

// MonitorLoopAsync — pure managed loop, no unsafe code needed:
private async Task MonitorLoopAsync(CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        // poll(fd, timeout_ms) — wake up every ~100 ms to check cancellation
        if (!UdevInterop.PollFd(_monitorFd, timeoutMs: 100))
            continue;

        var dev = UdevInterop.udev_monitor_receive_device(_monitor);
        if (dev == IntPtr.Zero) continue;

        try { DispatchAction(dev); }
        finally { UdevInterop.udev_device_unref(dev); }
    }
}
```

This is in contrast to macOS, where `IONotificationPortSetDispatchQueue` dispatches callbacks
on a GCD queue and the `[UnmanagedCallersOnly]` + `GCHandle` pattern is required.

---

### 4. `Interlocked.CompareExchange` double-start guard

`StartAsync` must guard against double-start with an atomic compare-exchange, matching the
contract enforced by `DeviceMonitorProviderContractTests.StartAsync_CalledTwice_ThrowsInvalidOperationException`:

```csharp
private int _started; // 0 = unstarted, 1 = started

public Task StartAsync(DeviceFilter filter, CancellationToken ct = default)
{
    ct.ThrowIfCancellationRequested();
    if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
        throw new InvalidOperationException(
            "StartAsync has already been called. Dispose and create a new monitor to restart.");
    // ...
}
```

---

### 5. `DisposeAsync` ordering

`DisposeAsync` must cancel and await the polling task before releasing the udev handles, to
guarantee no callbacks fire after the handles are freed:

```csharp
public async ValueTask DisposeAsync()
{
    if (_monitorCts is not null)
    {
        await _monitorCts.CancelAsync().ConfigureAwait(false);
        await (_monitorTask ?? Task.CompletedTask).ConfigureAwait(false);
        _monitorCts.Dispose();
        _monitorCts = null;
    }

    // Release udev handles in reverse registration order.
    // (No GCHandle to free — the Linux path has no native callback.)
    UdevInterop.udev_monitor_unref(_monitor);
    UdevInterop.udev_unref(_udev);
}
```


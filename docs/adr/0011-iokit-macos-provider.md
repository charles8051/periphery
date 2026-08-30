---
title: "ADR-0011: macOS Provider via IOKit + Notification Ports"
status: "Accepted"
date: "2026-03-10"
authors: "@charles8051 (proposal)"
tags: ["architecture", "decision"]
supersedes: ""
superseded_by: ""
---

# ADR-0011: macOS Provider via IOKit + Notification Ports

**Tracks:** macOS platform provider implementation  
**Supersedes:** (none — implements the macOS provider sketched in ARCHITECTURE.md §2.3)

---

## Context

`ARCHITECTURE.md §2.3` marks the macOS provider as "planned" and describes its shape:
`IOServiceGetMatchingServices` for enumeration, `IOServiceAddMatchingNotification` or
`IOServiceAddInterestNotification` on a run-loop or dispatch queue for monitoring, and IOService
property inspection for physical presence. This ADR formalises that choice, evaluates the
alternatives, and documents the implementation contract.

macOS exposes hardware devices through three distinct surfaces:

**1. IOKit (`IOKit.framework`)**  
The I/O Registry is the authoritative device tree on macOS. Every kernel device object
(`IOService`) is registered there with a full property dictionary. `IOServiceGetMatchingServices`
lets userspace enumerate a filtered snapshot; `IOServiceAddMatchingNotification` delivers
synchronous notifications when matching services appear or terminate. IOKit has been the correct
macOS device enumeration API since Mac OS X 10.0 and is fully supported on Apple Silicon.

**2. `system_profiler` / `ioreg` subprocess**  
The `system_profiler` and `ioreg` command-line tools wrap IOKit and produce structured output
(JSON, XML, or plain text). They are available on every macOS installation.

**3. Core Bluetooth (`CoreBluetooth.framework`)**  
For Bluetooth specifically, Apple's preferred API is `CoreBluetooth` (or `IOBluetooth` at the
IOKit level). `CBCentralManager` is the modern scanning/pairing surface, but it requires an
`NSRunLoop` and an app bundle with `NSBluetoothAlwaysUsageDescription` in `Info.plist`. For a
non-UI library, `IOBluetoothDevice` (IOKit level) is the more appropriate path.

**4. Network Extension / `SystemConfiguration.framework`**  
Network interface enumeration is available via `SCNetworkInterfaceCopyAll()` from
`SystemConfiguration.framework` or via the BSD `getifaddrs()` syscall — both are viable without
IOKit.

---

## Decision Drivers

| Driver | IOKit P/Invoke | Subprocess (`ioreg`) | WinRT-style `AVFoundation` / `CoreBluetooth` |
|---|---|---|---|
| AOT / trim safe | ✅ `[LibraryImport]`, no reflection | ❌ requires `Process` | 🟡 ObjC bridging has AOT constraints |
| No runtime service dependency | ✅ framework always present | ✅ binary always present | 🟡 requires runloop / app bundle |
| Event latency | ✅ synchronous I/O Registry notification | ❌ polling only | ✅ delegate callbacks |
| Property coverage | ✅ full I/O Registry property dict | ✅ same data via XML | 🟡 category-specific only |
| Zero extra NuGet dependencies | ✅ pure P/Invoke | ✅ pure managed `Process` | 🟡 `Microsoft.macOS` binding NuGet |
| Implementation complexity | 🟡 CF/IOKit interop structs | ✅ simple but fragile | ❌ ObjC object lifecycle |
| Works in daemons / CLI tools | ✅ no window/runloop required | ✅ | ❌ most APIs require NSRunLoop |

---

## Options Considered

### Option A — IOKit via `[LibraryImport]` ✅ Recommended

P/Invoke directly into `IOKit.framework` and `CoreFoundation.framework` using `[LibraryImport]`.
Both frameworks are always present on macOS; no additional NuGet package is needed.

The key IOKit enumeration loop:

```
IOServiceGetMatchingServices(kIOMasterPortDefault, matchingDict, &iterator)
  → IOIteratorNext(iterator)  // walk all matching IOService entries
    → IORegistryEntryCreateCFProperties(service, &properties, ...)
      → read kIOUSBProductString, kUSBVendorID, kUSBProductID, etc. from CFDictionary
    → IOObjectRelease(service)
  → IOObjectRelease(iterator)
```

The notification path uses an `IONotificationPort` backed by a Grand Central Dispatch (GCD)
source, avoiding any dependency on an `NSRunLoop`:

```
IONotificationPortCreate(kIOMasterPortDefault) → notificationPort
IONotificationPortSetDispatchQueue(notificationPort, dispatch_get_global_queue(...))
IOServiceAddMatchingNotification(notificationPort,
    kIOMatchedNotification,   // device appeared
    matchingDict,
    NotificationCallback, context,
    &iterator)
// Drain iterator once (initial set) then receive callbacks for future arrivals
// Repeat with kIOTerminatedNotification for removals
```

`dispatch_get_global_queue` requires P/Invoke into `libdispatch.dylib` (which is
`/usr/lib/system/libdispatch.dylib`, always present). This eliminates the `NSRunLoop` requirement
entirely — the provider works correctly inside .NET CLI applications, background `launchd`
services, and headless unit test processes.

---

### Option B — `system_profiler` / `ioreg` subprocess

Shell out to `ioreg -a -l -r` (XML output) or `system_profiler SPUSBDataType -json` and parse
the result.

**Rejected.** Subprocess overhead is prohibitive for a device enumeration library: each call
forks, execs, and waits for `ioreg` to walk the entire I/O Registry before returning.
`system_profiler` adds even more latency by aggregating across multiple data sources. The output
format is not a stable ABI (Apple does not document it as such). More fundamentally, this approach
gives no real-time monitoring path — the only option would be polling, reintroducing the same
latency problem that motivated moving away from WMI on Windows.

---

### Option C — `CoreBluetooth` / `AVFoundation` / `SystemConfiguration` per-category APIs

Use category-specific high-level frameworks: `CBCentralManager` for Bluetooth,
`AVCaptureDevice` for audio/camera, `SCNetworkInterfaceCopyAll()` for network.

**Partially adopted for network interfaces.** `getifaddrs()` (BSD syscall, always available) and
optionally `SCNetworkInterfaceCopyAll()` are used to populate `MacAddress`, `IPAddresses`, and
`Network` on `DeviceInfo` records for `DeviceCategory.Network`, because the IOKit
`IONetworkInterface` property dictionary does not reliably expose bound IP addresses.

**Rejected as the primary enumeration path.** Each framework covers only one category;
a unified device tree requires fanning out across six or more frameworks, each with its own
lifecycle, threading model, and entitlement requirements. IOKit is the common substrate that all
these frameworks ultimately read from.

---

### Option D — `NativeLibrary.TryLoad` + IOKit (runtime availability probe)

Same as Option A, but use `NativeLibrary.TryLoad("IOKit.framework/IOKit")` at provider
initialisation to fail gracefully if the framework is somehow absent.

**Adopted as a hardening detail within Option A,** not a separate option. The provider wraps its
`DllImport` resolution with a startup probe so that a `DllNotFoundException` is converted to a
`DeviceProviderException` with a diagnostic message, consistent with the Linux provider's
`libudev.so.1` availability check.

---

## Decision

**Adopt Option A** — implement `MacOSDeviceProvider` and `MacOSDeviceMonitorProvider` using
direct P/Invoke into `IOKit.framework` and `CoreFoundation.framework` for device enumeration and
notification. Use `dispatch_get_global_queue` (via `libdispatch.dylib`) as the dispatch queue for
`IONotificationPort`, avoiding any NSRunLoop dependency. Supplement with `getifaddrs()` for
network interface IP address resolution (Option C, scoped).

All P/Invoke declarations use `[LibraryImport]`. An `IOKitInterop.cs` file centralises all native
declarations, following the same pattern as `DevNodeHelper.cs` on Windows and `UdevInterop.cs` on
Linux.

The provider is guarded with `[SupportedOSPlatform("macos")]` and resolved only when
`OperatingSystem.IsMacOS()` is true.

---

## Implementation Sketch

### Interop declarations

```csharp
// IOKitInterop.cs (partial)
internal static partial class IOKitInterop
{
    private const string IOKit        = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string LibDispatch  = "/usr/lib/system/libdispatch.dylib";

    [LibraryImport(IOKit, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int IOServiceGetMatchingServices(
        uint masterPort, IntPtr matchingDict, out uint iterator);

    [LibraryImport(IOKit, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr IOServiceMatching(string name);

    [LibraryImport(IOKit)]
    internal static partial uint IOIteratorNext(uint iterator);

    [LibraryImport(IOKit)]
    internal static partial int IORegistryEntryCreateCFProperties(
        uint entry, out IntPtr properties, IntPtr allocator, uint options);

    [LibraryImport(IOKit, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr IORegistryEntryCreateCFProperty(
        uint entry, IntPtr key, IntPtr allocator, uint options);

    [LibraryImport(IOKit)]
    internal static partial int IOObjectRelease(uint obj);

    // Notification port
    [LibraryImport(IOKit)]
    internal static partial IntPtr IONotificationPortCreate(uint masterPort);

    [LibraryImport(IOKit)]
    internal static partial void IONotificationPortSetDispatchQueue(
        IntPtr notify, IntPtr queue);

    [LibraryImport(IOKit, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int IOServiceAddMatchingNotification(
        IntPtr notifyPort, string notificationType,
        IntPtr matchingDict,
        IOServiceMatchingCallback callback, IntPtr refCon,
        out uint notification);

    // CoreFoundation helpers
    [LibraryImport(CoreFoundation, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr CFStringCreateWithCString(
        IntPtr alloc, string cStr, uint encoding);

    [LibraryImport(CoreFoundation)]
    internal static partial void CFRelease(IntPtr cf);

    // libdispatch
    [LibraryImport(LibDispatch)]
    internal static partial IntPtr dispatch_get_global_queue(nint identifier, nuint flags);
}

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void IOServiceMatchingCallback(IntPtr refCon, uint iterator);
```

### Enumeration

```csharp
// MacOSDeviceProvider.EnumerateAsync()
foreach (var ioKitClass in MacOSCategoryMap.GetIOKitClasses(filter.Category))
{
    var matchingDict = IOKitInterop.IOServiceMatching(ioKitClass);
    int kr = IOKitInterop.IOServiceGetMatchingServices(0 /* kIOMasterPortDefault */,
        matchingDict, out uint iterator);
    if (kr != 0) throw new DeviceProviderException($"IOServiceGetMatchingServices failed: kr=0x{kr:X8}");

    try
    {
        uint service;
        while ((service = IOKitInterop.IOIteratorNext(iterator)) != 0)
        {
            try
            {
                var info = ToDeviceInfo(service, ioKitClass);
                if (info is not null && filter.Matches(info))
                    yield return info;
            }
            finally { IOKitInterop.IOObjectRelease(service); }
        }
    }
    finally { IOKitInterop.IOObjectRelease(iterator); }
}
```

### Change notifications

```csharp
// MacOSDeviceMonitorProvider.StartAsync()
_notifyPort = IOKitInterop.IONotificationPortCreate(0);
var queue = IOKitInterop.dispatch_get_global_queue(0 /* QOS_CLASS_DEFAULT */, 0);
IOKitInterop.IONotificationPortSetDispatchQueue(_notifyPort, queue);

foreach (var ioKitClass in MacOSCategoryMap.GetIOKitClasses(_filter.Category))
{
    // Arrival
    var arrivedDict = IOKitInterop.IOServiceMatching(ioKitClass);
    IOKitInterop.IOServiceAddMatchingNotification(_notifyPort,
        "IOServiceMatched", arrivedDict,
        OnDeviceArrived, GCHandle.ToIntPtr(_selfHandle), out uint arrivedIter);
    DrainIterator(arrivedIter, fireEvents: false);  // initial set

    // Removal
    var removedDict = IOKitInterop.IOServiceMatching(ioKitClass);
    IOKitInterop.IOServiceAddMatchingNotification(_notifyPort,
        "IOServiceTerminate", removedDict,
        OnDeviceRemoved, GCHandle.ToIntPtr(_selfHandle), out uint removedIter);
    DrainIterator(removedIter, fireEvents: false);
}
```

---

## Property Mapping

| `DeviceInfo` property | IOKit property key | Source |
|---|---|---|
| `Id` | `IORegistryEntryID` (unique 64-bit) | `IORegistryEntryGetRegistryEntryID` |
| `Name` | `kUSBProductString` / `IOHIDProductKey` | `IORegistryEntryCreateCFProperty` |
| `Manufacturer` | `kUSBVendorString` / `IOHIDManufacturerKey` | same |
| `VendorId` | `idVendor` (USB) / `kHIDVendorIDKey` | decimal integer CF property |
| `ProductId` | `idProduct` (USB) / `kHIDProductIDKey` | decimal integer CF property |
| `SerialNumber` | `kUSBSerialNumberString` | string CF property |
| `Category` | IOKit class name → `MacOSCategoryMap` | |
| `BusType` | Inferred from IOKit class hierarchy | e.g. `IOUSBDevice` → `BusType.Usb` |
| `IsConnected` | `sessionID` property present && service not terminated | IOKit notification state |
| `MacAddress` | `IOMACAddress` (as `NSData` / CFData, 6 bytes) | `IONetworkController` |
| `IPAddresses` | `getifaddrs()` correlated by interface name | BSD syscall |
| `Driver` | `IOMatchedPersonality` → `CFBundleIdentifier` | CF property |
| `DriverVersion` | `CFBundleVersion` of matched kext bundle | CF property |
| `DisplayResolution` | `DisplayProductName`, `IODisplayPrefsKey` | `IODisplayConnect` |
| `BatteryChargePercent` | `CurrentCapacity` / `MaxCapacity` | `AppleSmartBattery` |
| `BatteryStatus` | `IsCharging`, `ExternalConnected` | `AppleSmartBattery` |

---

## Category Mapping

A `MacOSCategoryMap` file (parallel to `DeviceClassGuids.cs` and `LinuxCategoryMap.cs`) maps
`DeviceCategory` values to IOKit class name strings:

| `DeviceCategory` | IOKit class(es) | Notes |
|---|---|---|
| `Usb` | `IOUSBDevice` | `IOUSBHostDevice` on macOS 12+ |
| `Bluetooth` | `IOBluetoothDevice` | `IOBluetoothHIDDriver` for HID BT |
| `Network` | `IONetworkInterface` | IP info from `getifaddrs()` |
| `Display` | `IODisplayConnect` | |
| `Hid` | `IOHIDDevice` | |
| `Keyboard` | `IOHIDDevice` | filtered by `kHIDUsage_GD_Keyboard` |
| `Mouse` | `IOHIDDevice` | filtered by `kHIDUsage_GD_Mouse` |
| `Audio` | `IOAudioDevice` | |
| `Storage` | `IOMedia` | |

---

## Consequences

**Positive:**

- No NuGet package additions — `IOKit.framework` and `CoreFoundation.framework` are system
  frameworks present on every macOS installation since 10.0.
- `[LibraryImport]` keeps the provider fully AOT/trim safe and works on Apple Silicon (arm64)
  without any extra configuration.
- `IONotificationPort` + `dispatch_get_global_queue` delivers synchronous kernel-level
  notifications with no NSRunLoop requirement — the provider works in CLI tools, background
  services, and `launchd` daemons.
- The I/O Registry is the authoritative source; property coverage is a superset of what any
  higher-level framework exposes for the same device.

**Negative / risks:**

- **CF object lifetime discipline.** Every `IORegistryEntryCreateCFProperty`,
  `IOServiceMatching`, and `CFStringCreateWithCString` call returns a `+1 retain` CF object that
  must be explicitly `CFRelease`d. Wrappers following the `SafeHandle` pattern (analogous to
  Windows `SafeHandle`) should be introduced to enforce this at compile time.
- **GCHandle pinning for callbacks.** The `IOServiceMatchingCallback` delegate must be kept alive
  for the lifetime of the notification registration via a `GCHandle`. Releasing the handle early
  causes a native callback into garbage-collected memory — a hard-to-reproduce crash.
- **`IOUSBDevice` deprecation on macOS 12+.** Apple deprecated `IOUSBDevice` and `IOUSBInterface`
  in favour of `IOUSBHostDevice` and `IOUSBHostInterface` (macOS 10.15+). The provider must
  query both classes and deduplicate by `IORegistryEntryID` to maintain full coverage across
  macOS 10.15–15.x.
- **Entitlements for Bluetooth.** Enumerating `IOBluetoothDevice` in a sandboxed application
  requires the `com.apple.security.device.bluetooth` entitlement. The library itself is not
  sandboxed, but consuming applications distributed via the Mac App Store are. Document this
  boundary; the provider should surface a clear `DeviceProviderException` with a diagnostic
  message when the entitlement check fails (indicated by `kr == kIOReturnNotPermitted`).
- **Integer IOKit object handles (`io_object_t` is `uint`).** Every `IOIteratorNext` and
  `IOObjectRelease` pair must be tracked; a missed release leaks a kernel object. Follow the same
  `try/finally` discipline established in the Linux provider.
- **Correlation between IOKit and BSD network state.** `IONetworkInterface` in the I/O Registry
  does not directly expose bound IP addresses — these live in the BSD networking stack. The
  provider must correlate the IOKit interface name (e.g. `"en0"`) with `getifaddrs()` results.
  The correlation is by name string and is best-effort; a device that changes names between
  enumeration and `getifaddrs()` call will produce a `DeviceInfo` with `IPAddresses` set to
  `null`. This is a known limitation; document it in the XML doc for `DeviceInfo.IPAddresses`.

---

## Amendments (2026-07-14)

The following corrections and additions were identified after benchmarking the macOS design against
the finalised Windows implementation (ADR-0009 + ADR-0012). They update the implementation
contract before coding begins.

---

### 1. `IOServiceMatchingCallback` delegate is not NativeAOT-safe (critical)

The original sketch declares:

```csharp
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void IOServiceMatchingCallback(IntPtr refCon, uint iterator);
```

This is the same managed-delegate anti-pattern that was removed from the Windows implementation
(the `CmNotifyCallback` delegate). `[LibraryImport]` falls back to
`Marshal.GetFunctionPointerForDelegate` for managed delegate parameters, which is reflection-based
and not AOT-safe.

**Replace with the `[UnmanagedCallersOnly]` + `GCHandle` pattern** established in ADR-0012 and
implemented in `WindowsDeviceMonitorProvider`:

```csharp
// IOKitInterop.cs — mark class unsafe; use function pointer in signature:
internal static unsafe partial class IOKitInterop
{
    [LibraryImport(IOKit, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int IOServiceAddMatchingNotification(
        IntPtr notifyPort,
        string notificationType,
        IntPtr matchingDict,
        delegate* unmanaged[Cdecl]<IntPtr, uint, void> callback,
        IntPtr refCon,
        out uint notification);
}

// MacOSDeviceMonitorProvider — AOT-safe static shim:
private GCHandle _selfHandle;

[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
private static void MatchedNotificationShim(IntPtr refCon, uint iterator)
{
    var self = (MacOSDeviceMonitorProvider)GCHandle.FromIntPtr(refCon).Target!;
    self.OnDeviceMatched(iterator);
}

[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
private static void TerminatedNotificationShim(IntPtr refCon, uint iterator)
{
    var self = (MacOSDeviceMonitorProvider)GCHandle.FromIntPtr(refCon).Target!;
    self.OnDeviceTerminated(iterator);
}
```

`_selfHandle` is allocated once in `StartAsync` before any notification is registered and freed
in `DisposeAsync` after all IOKit handles are released:

```csharp
// StartAsync:
_selfHandle = GCHandle.Alloc(this);
// ... register notifications passing GCHandle.ToIntPtr(_selfHandle) as refCon ...

// DisposeAsync:
IOKitInterop.IONotificationPortDestroy(_notifyPort);
if (_selfHandle.IsAllocated) _selfHandle.Free();
```

One `GCHandle` suffices regardless of how many notification registrations are active, because all
shims receive the same `refCon` value.

---

### 2. `IOServiceAddInterestNotification` for soft events and property changes

The original sketch covers only `kIOMatchedNotification` (hard arrive) and `kIOServiceTerminate`
(hard remove). Soft connect/disconnect (driver suspend/resume) and property changes require
`IOServiceAddInterestNotification` with `kIOGeneralInterest`, called once per discovered service
after the initial drain:

```csharp
// After DrainIterator for the matched notification:
uint service;
while ((service = IOKitInterop.IOIteratorNext(arrivedIter)) != 0)
{
    IOKitInterop.IOServiceAddInterestNotification(
        _notifyPort,
        service,
        "IOGeneralInterest",
        &InterestNotificationShim,
        GCHandle.ToIntPtr(_selfHandle),
        out uint interestNotification);
    // store interestNotification handle for release in DisposeAsync
    IOKitInterop.IOObjectRelease(service);
}

[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
private static void InterestNotificationShim(
    IntPtr refCon, uint service, uint messageType, IntPtr messageArgument)
{
    var self = (MacOSDeviceMonitorProvider)GCHandle.FromIntPtr(refCon).Target!;
    self.OnServiceInterest(service, messageType, messageArgument);
}

private void OnServiceInterest(uint service, uint messageType, IntPtr messageArgument)
{
    // kIOMessageServiceIsSuspended  (0xe0000100-ish) → DeviceDisconnected (soft)
    // kIOMessageServiceIsTerminated (0xe0000110-ish) → DeviceDisappeared
    // kIOMessageServicePropertyChange             → DevicePropertyChanged
    //
    // For PropertyChange: read fresh DeviceInfo, run DeviceInfoDiff.Compute, fire event.
}
```

`IOServiceAddInterestNotification` requires an additional P/Invoke declaration:

```csharp
[LibraryImport(IOKit, StringMarshalling = StringMarshalling.Utf8)]
internal static partial int IOServiceAddInterestNotification(
    IntPtr notifyPort,
    uint service,
    string interestType,
    delegate* unmanaged[Cdecl]<IntPtr, uint, uint, IntPtr, void> callback,
    IntPtr refCon,
    out uint notification);
```

---

### 3. `DevicePropertyChanged` via `DeviceInfoDiff.Compute`

When `OnServiceInterest` receives `kIOMessageServicePropertyChange`, the provider must:

1. Re-read properties from `IORegistryEntryCreateCFProperties(service, ...)`.
2. Look up the previous `DeviceInfo` snapshot in `_lastKnownDevices` (keyed by `IORegistryEntryID`).
3. Call `DeviceInfoDiff.Compute(previous, current)`.
4. Fire `DevicePropertyChanged` only if the diff is non-empty and update the cache.

`_lastKnownDevices` must be seeded in `StartAsync` after the initial notification drain. Use the
same `Dictionary<string, DeviceInfo>` / `_cacheLock` pattern as the Windows implementation.

---

### 4. `Interlocked.CompareExchange` double-start guard

`StartAsync` must include the atomic double-start guard, consistent with
`DeviceMonitorProviderContractTests.StartAsync_CalledTwice_ThrowsInvalidOperationException`:

```csharp
private int _started; // 0 = unstarted, 1 = started

public unsafe Task StartAsync(DeviceFilter filter, CancellationToken ct = default)
{
    ct.ThrowIfCancellationRequested();
    if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
        throw new InvalidOperationException(
            "StartAsync has already been called. Dispose and create a new monitor to restart.");
    // ...
}
```

`StartAsync` must be marked `unsafe` (for `&MatchedNotificationShim` function pointer address),
consistent with `WindowsDeviceMonitorProvider.StartAsync`. The `async` methods `DisposeAsync` and
any interest-notification handlers must NOT be marked `unsafe`; async and unsafe cannot coexist
in the same method.

---

### 5. Update to GCHandle pinning risk note

The "Negative / risks" item *"GCHandle pinning for callbacks"* referenced the managed delegate
approach, which is superseded by amendment 1. The corrected note is:

> **`GCHandle` for `[UnmanagedCallersOnly]` shims.** The provider instance must be kept
> reachable from native code for the lifetime of every notification registration. `GCHandle.Alloc`
> creates a strong reference; the handle must be freed in `DisposeAsync` *after* all IOKit
> notification handles are released (which drains any in-flight callbacks). Use
> `_selfHandle.IsAllocated` as a guard to make `DisposeAsync` idempotent.


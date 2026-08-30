---
title: "ADR-0009: Migrate Windows Provider from WMI to SetupAPI / cfgmgr32"
status: "Accepted"
date: "3/10/2026"
authors: "@charles8051 (proposal)"
tags: ["architecture", "decision"]
supersedes: ""
superseded_by: ""
---

# ADR-0009: Migrate Windows Provider from WMI to SetupAPI / cfgmgr32

**Tracks:** Windows platform provider implementation  
**Supersedes:** (none — extends the Windows provider described in ARCHITECTURE.md §2.3)

---

## Context

The current Windows provider (`WindowsDeviceProvider`, `WindowsDeviceMonitorProvider`) is built on
`System.Management` — the .NET wrapper around the WMI COM server. WMI has been the path of least
resistance for device enumeration on Windows since .NET Framework days, and it works. But it carries
a set of structural constraints that compound as the library matures:

**1. `System.Management` is not trim/AOT safe.**  
The assembly is reflection-heavy by design (WMI is a late-binding COM API). This permanently blocks
Periphery from being used in NativeAOT applications and increases publish size in trimmed builds due
to rooting the COM interop infrastructure. `PublishAot=false` is already required in
`scripts/device-dump.cs` as a direct consequence.

**2. The WMI service is a runtime dependency.**  
`winmgmt` must be running. In hardened enterprise environments, locked-down VMs, Windows IoT
builds, and some container configurations, WMI is disabled or restricted. The provider silently
fails in these scenarios with a `COMException` that is difficult to distinguish from a transient
error.

**3. WMI events are polling-based under the hood.**  
`__InstanceCreationEvent` / `__InstanceDeletionEvent` watchers use a `WITHIN` polling interval
(typically 1–2 seconds). This introduces noticeable event latency for fast plug/unplug cycles and
for Bluetooth device transitions. The actual device plug/unplug notification from the kernel is
synchronous — WMI adds an artificial delay.

**4. `Win32_PnPEntity` is a secondary projection.**  
The WMI class reads from the same SetupAPI property store that the Win32 kernel APIs expose
directly. Every property access goes through an extra COM marshalling hop. Some properties
(interface GUIDs, device capabilities flags, detailed USB descriptors) are not exposed through
`Win32_PnPEntity` at all, requiring separate WMI queries or the P/Invoke calls we already make via
`DevNodeHelper`.

**5. `DevNodeHelper` already proves the alternative works.**  
The existing `DevNodeHelper.cs` uses `[LibraryImport]` into `cfgmgr32.dll` for device-node status
flags — the same DLL that owns device enumeration and property retrieval. Every pattern needed for
a full provider already exists there in miniature.

---

## Decision Drivers

| Driver | WMI (current) | SetupAPI / cfgmgr32 |
|---|---|---|
| AOT / trim safe | ❌ | ✅ `[LibraryImport]`, no reflection |
| No runtime service dependency | ❌ WMI service required | ✅ Win32 API, always available |
| Event latency | ❌ ~1–2 s polling | ✅ Synchronous kernel notification |
| Property coverage | ❌ Limited to `Win32_PnPEntity` fields | ✅ Full `DEVPROPKEY` property store |
| Zero extra NuGet dependencies | ❌ `System.Management` package | ✅ Pure P/Invoke |
| Implementation complexity | ✅ High-level managed API | ❌ Verbose Win32 surface |
| Existing proof of concept | ✅ Entire provider | ✅ `DevNodeHelper` covers the pattern |

---

## Options Considered

### Option A — Keep WMI (status quo)

Continue using `System.Management`. Incrementally improve property coverage by supplementing with
`DevNodeHelper` calls.

**Rejected because:** AOT incompatibility is a hard architectural ceiling, not a tunable parameter.
The WMI service dependency becomes a support liability as the library targets more deployment
environments. Patching around WMI's limitations always adds more P/Invoke calls, which progressively
undermines the reason for using WMI in the first place.

---

### Option B — SetupAPI + cfgmgr32 via `[LibraryImport]` ✅ Recommended

Replace the WMI provider with a provider built entirely on direct Win32 P/Invoke:

| Concern | API |
|---|---|
| Enumerate devices | `SetupDiGetClassDevs` + `SetupDiEnumDeviceInfo` |
| Read typed properties | `CM_Get_DevNode_Property` with `DEVPROPKEY` |
| Category → class GUID mapping | Existing `DeviceClassGuids.cs` (unchanged) |
| Physical connection state | Existing `DevNodeHelper.IsDeviceConnected` (unchanged) |
| Device arrival / removal events | `CM_Register_Notification` (cfgmgr32, Vista+) |
| Interface arrival (e.g. COM ports) | `CM_Register_Notification` with `CM_NOTIFY_FILTER_TYPE_DEVICEINTERFACE` |

`CM_Register_Notification` is the modern replacement for the legacy
`RegisterDeviceNotification` + hidden message window pattern. It works in services,
console applications, and WinUI processes without requiring a window handle — a direct requirement
for a library-level API.

The `DEVPROPKEY`-based property store maps cleanly to the typed `DeviceInfo` fields introduced
in recent refactors:

| `DeviceInfo` property | `DEVPROPKEY` |
|---|---|
| `Name` | `DEVPKEY_Device_FriendlyName` / `DEVPKEY_Device_DeviceDesc` |
| `Manufacturer` | `DEVPKEY_Device_Manufacturer` |
| `ClassName` | `DEVPKEY_Device_Class` |
| `ClassGuid` | `DEVPKEY_Device_ClassGuid` |
| `Driver` | `DEVPKEY_Device_Service` |
| `DriverVersion` | `DEVPKEY_Device_DriverVersion` |
| `LocationPath` | `DEVPKEY_Device_LocationPaths` |
| `ParentId` | `DEVPKEY_Device_Parent` |
| `ContainerId` | `DEVPKEY_Device_ContainerId` |
| `Properties["HardwareID"]` | `DEVPKEY_Device_HardwareIds` |
| `Properties["CompatibleID"]` | `DEVPKEY_Device_CompatibleIds` |

---

### Option C — WinRT `Windows.Devices.Enumeration`

Use `DeviceInformation.FindAllAsync()` and `DeviceWatcher` from the WinRT API surface, accessed
via `Microsoft.Windows.SDK.NET`.

**Rejected for now, revisit later.** WinRT activation requires COM and carries its own AOT
constraints (workable, but requires careful handling). The `Microsoft.Windows.SDK.NET` package adds
a non-trivial dependency with its own versioning lifecycle. The WinRT `DeviceWatcher` API maps
very naturally to Periphery's own `DeviceWatcher` semantics and the WinRT property store exposes
`DeviceInformation.Properties` in a format that almost directly feeds `DeviceInfo`. This option
is worth a dedicated ADR when the library is ready to target UWP / WinUI packaging scenarios.

---

### Option D — Hybrid: WinRT events + SetupAPI properties

Use WinRT `DeviceWatcher` for change notifications (synchronous, well-tested, high coverage) and
`CM_Get_DevNode_Property` for property retrieval (avoids the WinRT property bag translation
overhead).

**Rejected.** Takes on both the complexity of Option B and the dependency of Option C without a
clear advantage over either individually. Introduces a split-ownership problem for the event source.

---

## Decision

**Adopt Option B.** Rewrite `WindowsDeviceProvider` and `WindowsDeviceMonitorProvider` using
`SetupDiGetClassDevs` / `SetupDiEnumDeviceInfo` for enumeration, `CM_Get_DevNode_Property` for
property retrieval, and `CM_Register_Notification` for real-time change events. All P/Invoke
declarations use `[LibraryImport]` (not `[DllImport]`).

`DevNodeHelper.cs` is promoted from a single-purpose helper into the core infrastructure file for
all Win32 interop, and is expanded to cover enumeration and notification. `WqlQuery.cs` is retired.
`System.Management` is removed as a package reference.

The provider's public contract — `IDeviceProvider` and `IDeviceMonitorProvider` — does not change.
No changes to `DeviceInfo`, `DeviceFilter`, `DeviceWatcher`, or any public API are required.

---

## Implementation Sketch

### Enumeration

```csharp
// For each category's class GUID:
var devInfoSet = SetupDiGetClassDevs(ref classGuid, null, IntPtr.Zero,
    DIGCF_PRESENT | DIGCF_ALLCLASSES);

var devInfo = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
for (int i = 0; SetupDiEnumDeviceInfo(devInfoSet, i, ref devInfo); i++)
{
    var name      = GetStringProperty(devInfoSet, devInfo, DEVPKEY_Device_FriendlyName);
    var classGuid = GetGuidProperty(devInfoSet, devInfo, DEVPKEY_Device_ClassGuid);
    // ... build DeviceInfo
    yield return device;
}
```

### Change notifications

```csharp
// Service- and console-friendly; no HWND required.
var filter = new CM_NOTIFY_FILTER
{
    cbSize     = Marshal.SizeOf<CM_NOTIFY_FILTER>(),
    FilterType = CM_NOTIFY_FILTER_TYPE_DEVICEINTERFACE,
};
CM_Register_Notification(ref filter, context, NotificationCallback, out _hNotify);

static int NotificationCallback(IntPtr hNotify, IntPtr context,
    CM_NOTIFY_ACTION action, ref CM_NOTIFY_EVENT_DATA eventData, int eventDataSize)
{
    // action == CM_NOTIFY_ACTION_DEVICEINTERFACEARRIVAL / _DEVICEINTERFACEREMOVAL
    // raise DeviceAppeared / DeviceDisappeared
}
```

---

## Consequences

**Positive:**

- Removes the `System.Management` NuGet dependency — leaner dependency graph, no WMI service
  requirement.
- Unblocks NativeAOT publishing for Windows consumers.
- Event latency drops from ~1–2 seconds to kernel-synchronous notification.
- `DEVPKEY` property coverage is a superset of what `Win32_PnPEntity` exposes — enables future
  `DeviceInfo` properties without provider-side workarounds.
- `DevNodeHelper.cs` patterns are already understood by the codebase; the expansion is incremental.

**Negative / risks:**

- **Implementation volume.** SetupAPI is a verbose C-style API. A full provider is substantially
  more code than the WMI version. The `DEVPROPKEY` / `SP_DEVINFO_DATA` interop structs require
  careful marshalling and test coverage.
- **Buffer-management discipline.** Property retrieval follows a two-call pattern (first call for
  required buffer size, second call for data) that must be implemented consistently to avoid heap
  corruption or truncation. This is the primary risk — a helper method must encapsulate it.
- **Error handling contract changes.** WMI surfaces errors as `ManagementException`. The new
  provider surfaces `Win32Exception` (from `Marshal.GetLastWin32Error()`). `DeviceProviderException`
  wraps both, so the public contract is unchanged, but the inner exception type changes.
- **`WqlQuery.cs` is retired.** Any future consumer using the internal `WqlQuery` type (e.g.
  integration tests) must be updated.
- **`System.Management` drop is a breaking change for callers who depend on
  `Properties["RawStatus"]` being a WMI CIM status string.** The SetupAPI equivalent is the
  `CM_PROB_*` problem code from `CM_Get_DevNode_Status`, which maps cleanly to `DeviceStatus` but
  doesn't produce an identical raw string. Mitigate by updating the `WellKnownProperties.RawStatus`
  doc comment.

---

## Implementation Notes

The following issues were identified during post-implementation review and corrected before
adopting this provider as the model for the Linux and macOS implementations.

**`DisposeAsync` race condition (fixed)**
The original implementation used a plain `bool _disposed` field with a non-atomic read-then-set
pattern. Two concurrent `DisposeAsync` calls could both pass the `if (_disposed)` check and both
call `CM_Unregister_Notification` with the same handle. Fixed by introducing `CmNotifyHandle`.

**`CmNotifyHandle : SafeHandle` added**
The notification handle (`nint _notifyHandle`) was promoted to a `SafeHandle` subclass nested
inside `DevNodeHelper`. This achieves three things simultaneously: `SafeHandle.Dispose()` is
thread-safe and idempotent (eliminates the race), `ReleaseHandle()` ensures
`CM_Unregister_Notification` is called even if `DisposeAsync` is never invoked (finalizer safety),
and the return code from `CM_Unregister_Notification` is now checked. The `bool _disposed` field
and the `_disposed` guard in `OnDeviceNotification` were removed; `CM_Unregister_Notification`
guarantees no callbacks fire after it returns.

**Redundant `CM_Locate_DevNode` calls eliminated**
`ToDeviceInfo(int devInst, string instanceId)` already receives `devInst` from enumeration, but
`GetProblemCode` and `IsDeviceConnected` re-located the device by ID string, each calling
`CM_Locate_DevNode` internally. Internal `devInst`-based overloads
(`GetProblemCode(int)`, `IsDeviceConnected(int)`) were added to `DevNodeHelper`; the public
string-based overloads now delegate to them. `ToDeviceInfo` uses the `devInst` overloads directly.

**No-op `await Task.CompletedTask.ConfigureAwait(false)` removed**
Awaiting an already-completed task is synchronous regardless of `ConfigureAwait`; the line did not
shift execution to the thread pool as the comment implied. It was removed. The method remains
`async IAsyncEnumerable<T>` (required by the language for async iterators using `yield return`);
CS1998 is suppressed with a targeted `#pragma` at the method site.

**`WqlQuery.cs` deleted**
The file was left on disk as an empty file after the WMI provider was removed. It was not
included in the project but created noise in the file tree. Deleted.

**Logging on dropped arrival events**
`HandleDeviceArrival` silently discarded arrivals when `TryBuildDeviceInfo` returned `null`
(e.g. driver not yet loaded). A `LogDebug` entry was added so the dropped event is visible in
diagnostics without adding noise at `LogWarning` level.

---

## Post-Implementation Review (2026-07-14)

The following issues were identified in a subsequent code review of the SetupAPI provider before
using it as the template for the Linux and macOS providers. Items are grouped by severity; their
resolution status reflects the state of the codebase at the time of writing.

---

### Real Bugs

**1. `StartAsync` violates the monitor contract — double-start not guarded** *(resolved by ADR-0012)*

`DeviceMonitorProviderContractTests` has an explicit rule: a second call to
`StartAsync(DeviceFilter, CancellationToken)` must throw `InvalidOperationException` — double-start
is a programming error. The original implementation ignored this: it silently registered a
duplicate `CM_Register_Notification`, overwrote `_notifyHandle`, and permanently leaked the first
notification handle.

Fixed in ADR-0012 by adding `Interlocked.CompareExchange(ref _started, 1, 0)` at the top of
`StartAsync`, which atomically guards the transition from unstarted (0) to started (1) and throws
`InvalidOperationException` if the exchange fails.

---

**2. `CmNotifyCallback` delegate is not NativeAOT-safe** *(resolved by ADR-0012)*

`[LibraryImport]` cannot generate code for managed delegate type parameters — it falls back to
`Marshal.GetFunctionPointerForDelegate`, a reflection-based path that is not safe under NativeAOT.
This was the actual reason `device-dump.cs` still carried `#:property PublishAot=false` even after
`System.Management` was removed.

The correct AOT pattern is `[UnmanagedCallersOnly]` on a static method with the provider instance
passed through the native `pContext` parameter via `GCHandle`:

```csharp
// DevNodeHelper.cs — updated P/Invoke signature:
[LibraryImport("cfgmgr32.dll")]
internal static unsafe partial int CM_Register_Notification(
    ref CM_NOTIFY_FILTER pFilter,
    nint pContext,
    delegate* unmanaged[Stdcall]<nint, nint, int, nint, int, int> pCallback,
    out nint pNotifyContext);

// WindowsDeviceMonitorProvider.cs — AOT-safe shim:
private GCHandle _selfHandle;

[UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
private static int NotificationShim(
    nint hNotify, nint context, int action, nint eventData, int eventDataSize)
{
    var self = (WindowsDeviceMonitorProvider)GCHandle.FromIntPtr(context).Target!;
    return self.OnDeviceNotification(hNotify, action, eventData, eventDataSize);
}
```

`_selfHandle` is allocated in `StartAsync` and freed in `DisposeAsync`. This pattern is also the
template the Linux (`udev_monitor` fd + poll loop) and macOS (`IOServiceMatchingCallback` +
`GCHandle.ToIntPtr`) providers will need to follow — fixing it on Windows first ensures the
template is correct. `#:property PublishAot=false` was removed from `device-dump.cs` as a
consequence. `CmNotifyCallback` delegate type and its `[UnmanagedFunctionPointer]` attribute were
deleted; `DevNodeHelper` is now marked `unsafe partial class` to allow the source generator to
emit the function-pointer implementation body.

---

### Inconsistencies

**3. `GetDevNodeStatus(string)` doesn't delegate like its neighbours** *(resolved)*

After the refactor, `IsDeviceConnected(string)` and `GetProblemCode(string)` both delegate to
`devInst`-based internal overloads. `GetDevNodeStatus(string)` still contains its own duplicated
two-step `CM_Locate_DevNode` + `CM_Get_DevNode_Status` logic; there is no corresponding
`GetDevNodeStatus(int devInst)` internal overload. Since this file is the template for the Linux
and macOS helpers, it should model the delegation pattern consistently.

---

**4. `DevicePropertyChanged` is an undocumented stub** *(addressed by ADR-0012)*

The event was declared on `IDeviceMonitorProvider` and implemented on `WindowsDeviceMonitorProvider`
but never fired. The interface-filter registration (`CM_NOTIFY_FILTER_TYPE_DEVICEINTERFACE`) only
delivers arrival and removal actions; property mutations require a second registration with
`CM_NOTIFY_FILTER_TYPE_DEVICEINSTANCE` combined with a periodic scan (cfgmgr32 has no
property-changed action). This was a known limitation but was undocumented, leaving Linux and macOS
implementors without guidance. ADR-0012 addresses both the documentation and the implementation:
the limitation is now explained in the class XML comment, and the instance filter + scan loop that
restores `DevicePropertyChanged` is specified and implemented there.

---

### Test Gap

**5. No concrete Windows subclass of the contract test suites** *(resolved)*

`DeviceProviderContractTests` and `DeviceMonitorProviderContractTests` are abstract base classes.
`FakeProviderContractTests` and `FakeMonitorProviderContractTests` exist, but there is no
`WindowsDeviceProviderContractTests` or `WindowsDeviceMonitorProviderContractTests`. This means the
real provider has never been verified against its own contract — including the double-start rule
that bug #1 violated. Since these contract suites are exactly the template that Linux and macOS
tests should subclass, Windows should have working examples first. This item is deferred until
hardware-in-the-loop CI is available; it should be added alongside the first Linux or macOS
provider so all three share the same contract scaffold from day one.

---

### Recommended ordering

Fix **#1** (double-start guard) and **#2** (AOT delegate → `[UnmanagedCallersOnly]`) before using
the Windows provider as a template, because both issues will recur verbatim in Linux and macOS
implementations. **#3** and **#4** are low-risk cleanup. **#5** can be added alongside the first
cross-platform provider since it requires hardware-in-the-loop CI.


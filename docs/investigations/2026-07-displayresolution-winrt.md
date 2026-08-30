# Investigation: `DisplayResolution` Always Null on Windows

**Date:** 2026-07-14  
**Status:** Resolved  
**Affected property:** `DeviceInfo.DisplayResolution`  
**Component:** `WindowsWinRTEnricher`

---

## Symptoms

`DeviceInfo.DisplayResolution` was always `null` for `DeviceCategory.Monitor` devices, even
on Windows 10/11 with the `net10.0-windows10.0.17763.0` TFM.

---

## Investigation

### Bug 1 — Wrong TFM in `Periphery.Examples`

The examples project targeted `net10.0` (generic). The WinRT enrichment path is inside
`#if WINDOWS10_0_17763_0_OR_GREATER`, which is only defined for the
`net*-windows10.0.17763.0` TFMs. On the generic TFM the entire enricher compiles to a
no-op stub, so `BuildAsync` returned an empty enricher and `Enrich()` was a pass-through.

**Fix:** Changed `Periphery.Examples` to target `net10.0-windows10.0.17763.0`.

---

### Bug 2 — `EnableWinRTEnrichment` flag defaulted to `false`

Even on the correct TFM, the enricher was gated behind
`WindowsProviderOptions.EnableWinRTEnrichment` which defaulted to `false`. Callers had no
public API to set it, so enrichment was silently skipped for everyone.

**Fix:** Removed the flag entirely. Enrichment now runs automatically on Windows 10 17763+
whenever the active `DeviceFilter` can match `Monitor` or `Battery` devices (determined by
the new `DeviceFilter.NeedsMonitorEnrichment` / `NeedsBatteryEnrichment` helpers). A
`DeviceCategory.Usb`-only query pays zero cost.

---

### Bug 3 — `Task.WhenAll` propagated the first `FromIdAsync` exception

`BuildDisplayMonitorMapAsync` used `Task.WhenAll` across all per-monitor tasks. If any one
task threw (which they all did — see Bug 4), `Task.WhenAll` propagated the first exception
and the entire method threw. The outer `try/catch` in `EnumerateAsync` swallowed it and
left `wrtEnricher` null, so enrichment was silently skipped.

**Fix:** Added per-task `try/catch` so individual monitor failures skip that entry without
aborting the whole map build.

---

### Bug 4 (root cause) — `DisplayMonitor.FromIdAsync` vs `DisplayMonitor.FromInterfaceIdAsync`

This was the core bug. `BuildDisplayMonitorMapAsync` called:

```csharp
DisplayMonitor? dm = await DisplayMonitor.FromIdAsync(di.Id).AsTask(ct);
```

`di.Id` is the WinRT **device interface path**, e.g.:

```
\\?\DISPLAY#ACR0507#5&837d20f&0&UID41217#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}
```

`DisplayMonitor.FromIdAsync` expects a **container device ID** — a different ID obtained
from a separate `DeviceInformation` enumeration using
`DisplayMonitor.GetDeviceSelector()` with `KindFilter = DeviceInformationKind.Device`.
Passing a device interface path to `FromIdAsync` always throws
`FileNotFoundException: Unable to find the specified file` (HRESULT `0x80070002`).

The correct API is `DisplayMonitor.FromInterfaceIdAsync(di.Id)`, which accepts the device
interface path directly. This API has been available since Windows 10 1903 (build 18362),
well within the `windows10.0.17763.0` minimum we already require.

**Diagnostic evidence (debugger tracepoints):**

```
di.Id  = "\\?\DISPLAY#ACR0507#5&837d20f&0&UID41217#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}"
instanceId = "DISPLAY\ACR0507\5&837d20f&0&UID41217"
→ FromIdAsync threw: Unable to find the specified file.
→ FromInterfaceIdAsync succeeded: NativeResolutionInRawPixels = {2560, 1440}
```

**Fix:** Replaced `DisplayMonitor.FromIdAsync(di.Id)` with
`DisplayMonitor.FromInterfaceIdAsync(di.Id)`.

---

### Additional improvement — `_monitors` field simplified to `IReadOnlyDictionary<string, Size>`

Since `NativeResolutionInRawPixels` is the only property we read from `DisplayMonitor`, the
map value type was changed from `DisplayMonitor` to `Size`. The resolution is extracted once
at build time inside the per-task lambda and stored directly. This removes the dependency on
the `DisplayMonitor` object lifetime and makes `EnrichMonitor` a simple dictionary lookup
with no further WinRT calls.

---

## Final state of `BuildDisplayMonitorMapAsync`

```csharp
private static async Task<IReadOnlyDictionary<string, Size>> BuildDisplayMonitorMapAsync(
    CancellationToken ct)
{
    string selector = DisplayMonitor.GetDeviceSelector();
    DeviceInformationCollection devices = await DeviceInformation
        .FindAllAsync(selector, s_instanceIdProp)
        .AsTask(ct)
        .ConfigureAwait(false);

    var tasks = devices
        .Where(di => di.Properties.TryGetValue("System.Devices.DeviceInstanceId", out _))
        .Select(async di =>
        {
            string instanceId = (string)di.Properties["System.Devices.DeviceInstanceId"];
            try
            {
                DisplayMonitor? dm = await DisplayMonitor.FromInterfaceIdAsync(di.Id).AsTask(ct)
                    .ConfigureAwait(false);
                var res = dm?.NativeResolutionInRawPixels;
                return (instanceId, size: res is { Width: > 0, Height: > 0 }
                    ? (Size?)new Size((int)res.Value.Width, (int)res.Value.Height)
                    : null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Debug.WriteLine($"[WinRTEnricher] FromInterfaceIdAsync failed for {instanceId}: {ex.Message}");
                return (instanceId, size: (Size?)null);
            }
        });

    var results = await Task.WhenAll(tasks).ConfigureAwait(false);
    return results
        .Where(r => r.size.HasValue)
        .ToDictionary(r => r.instanceId, r => r.size!.Value, StringComparer.OrdinalIgnoreCase);
}
```

Per-monitor tasks run in parallel via `Task.WhenAll`. On a 4-monitor machine, 4 concurrent
`FromInterfaceIdAsync` calls complete in parallel rather than sequentially.

---

## Also fixed in this session

- **`DisplayBounds` not implemented** — confirmed no Windows provider populates this field.
  Tracked separately; requires `QueryDisplayConfig` Win32 P/Invoke to get virtual desktop
  rectangle per monitor.

- **`Periphery.Examples` tracker lifetime bug** — `trackingWatcher` and `watcher2` were
  both declared `await using var` at top-level scope in `Program.cs`, so `trackingWatcher`
  was still alive (with `anyUsb`/`anyBluetooth` bound) when example 13 tried to re-attach
  those same trackers to `watcher2`, throwing `InvalidOperationException: This DeviceTracker
  is already bound to an active DeviceWatcher`. Fixed by wrapping each watcher in an explicit
  `await using (...) { }` block so disposal is scoped correctly.

- **`WinRT.Runtime.dll` shell property keys** — an intermediate attempt to read
  `System.Devices.DisplayMonitor.NativeResolutionHorizontal` / `...Vertical` as
  `DeviceInformation` properties caused `COMException` from `WinRT.Runtime.dll` because
  those property keys do not exist in the `DeviceInformation` property bag. The correct
  approach is `FromInterfaceIdAsync` (see above).

---

## Key WinRT API clarification

| API | Argument | Use case |
|---|---|---|
| `DisplayMonitor.FromIdAsync(id)` | Container device ID (`DeviceInformationKind.Device`) | Rarely needed; different enumeration selector required |
| `DisplayMonitor.FromInterfaceIdAsync(interfaceId)` | Device interface path (`\\?\DISPLAY#...#{guid}`) | **Correct API** — matches `DeviceInformation.FindAllAsync` with `GetDeviceSelector()` |

The MSDN documentation for `FromIdAsync` does not clearly state that it requires a container
ID rather than an interface ID. The `FileNotFoundException` error message
("Unable to find the specified file") is the only runtime signal, with no indication of the
ID format mismatch.

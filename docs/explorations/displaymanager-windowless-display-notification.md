# Is there a windowless replacement for the `WM_DISPLAYCHANGE` sink?

Status: exploration / research only. **Nothing implemented, nothing changed.**
Conclusion: **no — ADR-0066 Decision 1 stands.** Tracks
[issue #154](https://github.com/charles8051/periphery/issues/154) part 2.

## The question

[ADR-0066](../adr/0066-monitor-displayconfig-refresh-hook.md) Decision 1 gives
`WindowsDeviceMonitorProvider` a hidden **top-level** Win32 window
(`src/Periphery/Windows/WindowsDisplayChangeSink.cs`) that receives the
`WM_DISPLAYCHANGE` broadcast. It has three known costs:

1. It is a real top-level window and therefore visible to `EnumWindows`. It
   cannot be a message-only (`HWND_MESSAGE`) window: *"A message-only window
   enables you to send and receive messages. It is not visible, has no z-order,
   **cannot be enumerated, and does not receive broadcast messages**."*
   ([Window Features](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features)).
   `WM_DISPLAYCHANGE` itself is *"only sent to top-level windows"*
   ([WM_DISPLAYCHANGE](https://learn.microsoft.com/en-us/windows/win32/gdi/wm-displaychange)).
   So the premise ADR-0066 rejected message-only windows on is **confirmed by
   documentation** (confidence: **high**).
2. One window + one dedicated pump thread **per `DeviceWatcher`**, regardless of
   whether the caller cares about monitors.
3. In session 0 the window never receives the console session's broadcast, so
   the refresh is silently inert there (ADR-0066 Consequences).

The hypothesis under test: `Windows.Devices.Display.Core.DisplayManager` (WinRT)
exposes a `Changed` event with no HWND, so it could replace the sink.

## Verdict

**(c) Not viable.** Three independent reasons, in order of how load-bearing they are:

| # | Finding | Confidence |
|---|---|---|
| 1 | `DisplayManager.Changed` is documented to fire on **`DisplayAdapter` / `DisplayTarget` collection** changes. Rotation, source/target resolution and refresh rate are properties of **`DisplayPath`** (inside a `DisplayState`), not of `DisplayTarget`. The rotation-of-the-primary-panel case — the reason ADR-0066 exists — is **not a documented trigger**. | High (docs) / Medium (that it *never* fires) |
| 2 | Consuming it forces `net10.0-windows10.0.17763.0`+ on `Periphery`, which is exactly the TFM coupling [ADR-0018](../adr/0018-winrt-enrichment-tfm-coupling.md) removed and [ADR-0067](../adr/0067-single-target-net10.md) just simplified away. Nothing has changed to weaken that objection. | High |
| 3 | Session 0 behaviour is **undocumented**, and the surrounding API surface is explicitly *session*-scoped — so it very likely does not fix the session-0 gap either. | Medium |

The hypothesis was **half right**: the event exists and takes no HWND. It is the
wrong event.

---

## 1. Does `DisplayManager` expose a change notification, and what does it fire on?

**Yes — the member exists.**
[`DisplayManager.Changed`](https://learn.microsoft.com/en-us/uwp/api/windows.devices.display.core.displaymanager.changed),
typed `TypedEventHandler<DisplayManager, DisplayManagerChangedEventArgs>`.
Siblings on the same class: `Enabled`, `Disabled`, `PathsFailedOrInvalidated`
([DisplayManager class](https://learn.microsoft.com/en-us/uwp/api/windows.devices.display.core.displaymanager)).

**What it fires on — the verbatim documentation:**

> An event that is raised when system display hardware is added, removed, or
> modified. This can occur whenever the `DisplayAdapter` or `DisplayTarget`
> collections change. Use this event to detect these changes and call
> `GetCurrentAdapters` and/or `GetCurrentTargets` to get the updated collections.

That is a **topology/hardware** signal, not a mode signal. The remedial action
the docs prescribe (`GetCurrentAdapters` / `GetCurrentTargets`) returns *which
adapters and targets exist* — neither call surfaces a mode.

**Where mode and rotation actually live.** Not on `DisplayTarget`. They are
properties of
[`DisplayPath`](https://learn.microsoft.com/en-us/uwp/api/windows.devices.display.core.displaypath):
`Rotation` (*"how the display pipeline rotates the source frame buffer before
scanning out to the target"*), `SourceResolution`, `TargetResolution`,
`PresentationRate`, `Scaling`. A `DisplayPath` is reached through a
`DisplayState`, obtained via `TryReadCurrentStateForAllTargets()` /
`TryAcquireTargetsAndReadCurrentState(...)` — a *pull*, with no corresponding
push event on `DisplayManager`.

So rotating the primary panel at (0,0) changes a `DisplayPath` property while the
`DisplayAdapter` and `DisplayTarget` collections stay identical. On the
documented contract, `Changed` should not fire.

- Confidence that `Changed` covers **attach/detach**: **high**.
- Confidence that `Changed` does **not** cover **rotation/mode on an attached
  panel**: **medium-high**. It follows directly from the documented trigger and
  the object model, but Microsoft has not written "rotation does not raise
  Changed" anywhere, and "or modified" in the doc text is not defined. This is
  **inference from documentation, not an explicit statement** — and I found no
  empirical report either way in GitHub issues or StackOverflow.

**This is the decisive fact.** Attach/detach is already covered windowlessly by
the provider's existing `CM_Register_Notification` on `GUID_DEVINTERFACE_MONITOR`.
Swapping a hidden window for a WinRT dependency to obtain a signal we already
have — while losing the mode/rotation signal that motivated ADR-0066 — is a
strict regression.

### A secondary hazard, had it worked

[`DisplayManagerChangedEventArgs`](https://learn.microsoft.com/en-us/uwp/api/windows.devices.display.core.displaymanagerchangedeventargs)
carries `Handled` and `GetDeferral()`. A deferral only exists because the
**system waits for handlers to complete**. That is the same class of hazard as
`WM_DISPLAYCHANGE` being delivered by `SendMessage` — so ADR-0066 Decision 3
("the heavy work runs off the notification thread") would still be required, and
issue `#153`'s stall mode would still be reachable. Confidence: **high** that a
deferral implies a waited-on callback; **medium** on the precise blocking
semantics, which are undocumented.

## 2. Is it genuinely windowless?

**No HWND, no `CoreWindow`, no `DispatcherQueue` documented as required.**
`DisplayManager` is attributed `MarshalingBehavior(Agile)` and
`Threading(Both)` ([class page](https://learn.microsoft.com/en-us/uwp/api/windows.devices.display.core.displaymanager)),
so it does not pin itself to an apartment, and it is not on the
[WinRT-APIs-unsupported-in-desktop-apps](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/winrt-api-desktop-app-support)
list (neither the "requires `CoreWindow`" nor the "requires package identity"
section). Contrast
[`Windows.Graphics.Display.DisplayInformation`](https://learn.microsoft.com/en-us/uwp/api/Windows.Graphics.Display.DisplayInformation),
which *is* view-bound: *"Calling `GetForCurrentView` will always return the
single instance for the current thread's `CoreApplicationView`. An instance of
DisplayInformation can only be used from the thread on which it was created."*

Two real constraints do exist:

- **`Start()` is mandatory and has a subscription precondition.** *"DisplayManager
  events are not raised until you call `DisplayManager.Start`"*, and *"All callers
  of Start are required to have subscribed to `Enabled`, `Disabled`, `Changed`,
  and `PathsFailedOrInvalidated`. Start fails if there are no subscribers to any
  of those events."*
  ([Start](https://learn.microsoft.com/en-us/uwp/api/windows.devices.display.core.displaymanager.start)).
  Confidence: **high**.
- **Which thread the callback arrives on is undocumented.** The agile/both
  attributes imply that a subscriber created on an MTA thread (Periphery's
  thread-pool/cfgmgr32 world) would be called back on an RPC thread with no pump
  needed, per ordinary COM apartment rules — but Microsoft does not state this
  for this class. Confidence: **medium**, by inference. A COM apartment must
  still be initialised on some thread, which is a new process-wide concern
  Periphery does not have today.

## 3. Does it work in session 0 / inside a Windows service?

**Undocumented.** Neither the `DisplayManager` reference nor the desktop-app
support list mentions services, session 0, or non-interactive sessions.

The surrounding surface is nonetheless explicitly **session-scoped**, which is a
strong hint it inherits the same limitation as `WM_DISPLAYCHANGE`:

- [`Disabled`](https://learn.microsoft.com/en-us/uwp/api/windows.devices.display.core.displaymanager.disabled)
  is *"raised whenever **the current session's display stack** is disabled … such
  as switching Terminal Services sessions … **Most display APIs will fail while
  the session display stack is disabled.**"*
- [`DisplayManagerResult.RemoteSessionNotSupported`](https://learn.microsoft.com/en-us/uwp/api/windows.devices.display.core.displaymanagerresult)
  — *"the operation failed because **the current session** is currently in an
  unsupported remote desktop session that does not allow access to the display
  stack."*

Conclusion: **do not assume it fixes the session-0 gap.** Confidence that it is
session-scoped: **medium** (documented for adjacent members, never stated for
session 0 specifically). Confidence that session 0 is documented anywhere:
**high — it is not.** Settling this would require an experiment on a real
service, which this exploration did not run.

## 4. Minimum Windows version

`Windows 10, version 1809 (10.0.17763.0)` / `UniversalApiContract v7.0` — for
`DisplayManager`, `DisplayPath`, `DisplayManagerChangedEventArgs` and
`DisplayManagerResult` alike (all four reference pages agree). Confidence: **high**.

## 5. What would it cost `Periphery`?

This is where the objection is decisive independent of capability.

Calling WinRT from .NET 6+ requires a **Windows-version-specific TFM**:
*"Specify a Windows OS version-specific Target Framework Moniker (TFM) in your
project file. This adds a reference to the appropriate Windows SDK targeting
package at build time"*
([Call WinRT APIs in desktop apps](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/winrt-apis-desktop-apps)) —
i.e. `net10.0-windows10.0.17763.0` and a `Microsoft.Windows.SDK.NET.Ref`
reference. Confidence: **high**.

`src/Periphery/Periphery.csproj` is `<TargetFramework>net10.0</TargetFramework>`
(ADR-0067). The two ways forward are both bad:

| Option | Consequence |
|---|---|
| **Single-target `net10.0-windows10.0.17763.0`** | Any Linux/macOS consumer, and any plain-`net10.0` consumer, **cannot reference `Periphery` at all** (`NU1201`). Periphery stops being a cross-platform library. See [CsWinRT #1170](https://github.com/microsoft/CsWinRT/issues/1170), [NU1201](https://learn.microsoft.com/en-us/nuget/reference/errors-and-warnings/nu1201). |
| **Multi-target `net10.0;net10.0-windows10.0.17763.0`** | Reintroduces exactly ADR-0018's failure: *"Any consumer on a plain `net*` TFM received the no-op stub DLL regardless of which platform they ran on — monitor enrichment was silently absent for the majority of real consumers."* Plus it re-adds the multi-TFM matrix ADR-0067 just removed on the grounds that we do not test what we ship. |

**Has anything changed since ADR-0018?** No. The mechanism is unchanged
(WinRT projections still come from a Windows TFM), and the *reason* is the same
one ADR-0018 gave. If anything the objection is stronger now: ADR-0067 has just
committed to a single, tested TFM, and reversing that for one hidden window is
not a trade worth making.

ADR-0018's Alternative D (raw `RoGetActivationFactory` + hand-rolled COM vtables,
no TFM) is still technically open, and .NET 8's `[GeneratedComInterface]` makes
it less awful than it was when ADR-0018 was written. But it would only be worth the complexity
if the API delivered the rotation signal — and per §1 it does not.

## 6. Other windowless options

Capability is judged on one question only: **does it report rotation / mode
change on an already-attached panel?** Attach/detach is not interesting — the
provider already has it, windowlessly, from cfgmgr32.

| Mechanism | Windowless? | Rotation/mode on an attached panel? | Notes |
|---|---|---|---|
| **`IDXGIFactory7::RegisterAdaptersChangedEvent`** | **Yes** (signals a kernel `HANDLE`; a waiter thread, no HWND, no pump) | **No** | Documented as *"notification of changes whenever the **adapter enumeration state** changes"* — adapter-level, coarser than what cfgmgr32 already gives. Win10 1809+. ([docs](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_6/nf-dxgi1_6-idxgifactory7-registeradapterschangedevent)) Confidence: **high**. |
| **`CM_Register_Notification`** (any display filter) | **Yes** (already used) | **No** | The full [`CM_NOTIFY_ACTION`](https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/ne-cfgmgr32-cm_notify_action) set is interface arrival/removal, query-remove/remove-pending/remove-complete, custom event, instance enumerated/started/removed. **There is no property-change action** — the same gap ADR-0054 documented. No filter can yield a mode change. Confidence: **high**. |
| **WMI / `MSMonitorClass`, `WmiMonitorID`** | Yes | **No — and it is polling** | Monitor-mode changes surface only as intrinsic `__InstanceModificationEvent`, which requires a `WITHIN` clause: *"A polling interval is the interval that WMI uses to **poll** the data provider."* ([WITHIN clause](https://learn.microsoft.com/en-us/windows/win32/wmisdk/within-clause)). That is a hidden poll inside WMI — barred by [ADR-0054](../adr/0054-windows-property-freshness-events-over-polling.md), and ADR-0009 already moved off WMI. Confidence: **high**. |
| **`SetWinEventHook`** | **No** | **No** | *"The client thread that calls SetWinEventHook **must have a message loop** in order to receive events."* ([docs](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwineventhook)) — so it trades a window for a pump and keeps the pump. And no [event constant](https://learn.microsoft.com/en-us/windows/win32/winauto/event-constants) reports a display mode change; the range is UI/accessibility events. Strictly worse. Confidence: **high**. |
| **`Windows.Graphics.Display.DisplayInformation`** | **No** | Yes (`OrientationChanged`, `DpiChanged`) — but unreachable | Requires a `CoreApplicationView`; *"can only be used from the thread on which it was created"* ([docs](https://learn.microsoft.com/en-us/uwp/api/Windows.Graphics.Display.DisplayInformation)). It is per-*view*, i.e. it needs a UI thread **and** the WinRT TFM. Strictly worse than a hidden window. Confidence: **high**. |
| **`PowerSettingRegisterNotification`** (`DEVICE_NOTIFY_CALLBACK`) | **Yes** — and it also accepts `DEVICE_NOTIFY_SERVICE_HANDLE`, so it works in a service | **No** | Display-related setting GUIDs report *power state* (monitor on/off, console display state), never geometry. Useful precedent for "windowless + service-capable notification", useless for this signal. Win7+. ([docs](https://learn.microsoft.com/en-us/windows/win32/api/powersetting/nf-powersetting-powersettingregisternotification)) Confidence: **high** for the mechanism, **medium** for the exhaustiveness of "no geometry GUID". |

Not evaluated in depth, noted for completeness: a real-time **ETW** session on
`Microsoft-Windows-DxgKrnl` observes mode-set activity and is windowless, but it
needs elevation, is an unstable/undocumented event contract, and is far heavier
than one hidden window. Not recommended; not researched further.

**No mechanism found reports rotation/mode change on an attached panel without a
window.** As far as documented Windows APIs go, `WM_DISPLAYCHANGE` on a top-level
window appears to be the only general-purpose push signal for it.

## What this means for issue `#154` part 2

The mechanism is not the lever. The two costs in `#154` part 2 are addressable
without changing the OS signal at all:

- **Cost 2 (one window + one thread per `DeviceWatcher`)** is a Periphery
  lifetime choice, not an OS constraint. A process-wide, reference-counted sink
  shared by all `WindowsDeviceMonitorProvider` instances collapses *N* windows
  and *N* threads to one, with no API-surface or dependency change. This is the
  cheap, obviously-correct fix.
- **Cost 1 (visible to `EnumWindows`)** cannot be eliminated — a top-level window
  is the price of the broadcast — but it can be made **opt-out**, as `#154` itself
  proposes: an explicit option on the watcher/provider rather than a filter
  heuristic (the filter is empty whenever any tracker is registered,
  `src/Periphery/DeviceWatcher.cs`). A kiosk host that audits top-level windows
  and does not need monitor freshness turns it off and gets zero windows; the
  degraded behaviour is already implemented and tested (it is the same path as
  window-creation failure in `WindowsDisplayChangeSink.TryCreateWindow`).

Neither needs an ADR superseding ADR-0066 Decision 1.

## Open questions this exploration could not settle

1. Whether `DisplayManager.Changed` *empirically* fires on a rotation of an
   already-attached panel. Documentation says it should not; no empirical report
   was found in either direction. Only a hardware experiment would settle it —
   and even a positive result would not overcome §5.
2. Whether `DisplayManager` functions at all in session 0. Undocumented.
3. Which thread `Changed` is delivered on for an MTA subscriber. Inferred from
   the agile/both attributes; never stated.

---
title: "ADR-0058: Periphery.Monitor — DDC/CI monitor-control extension"
status: "Accepted"
status_note: "implemented 2026-06-12"
date: "2026-06-12 (amended 2026-06-12: display-mode control — resolution, orientation, refresh; see Amendment)"
authors: "@charles8051 (design draft)"
tags: ["architecture", "decision"]
supersedes: ""
superseded_by: ""
---

# ADR-0058: Periphery.Monitor — DDC/CI monitor-control extension

**Tracks:** (new package) `Periphery.Monitor`: `MonitorDevice`, `MonitorDeviceProxy`, `IMonitorBackend`, `IDisplayModeBackend`, `MccsCapabilities`, `VcpCode`, `DdcCiWire`, `DisplayMode`, `MonitorOrientation`; CLI `monitor` command group
**Related:** ADR-0013/0015/0018 (Windows monitor metadata enrichment — DisplayConfig), ADR-0044 (registry EDID fallback for `MonitorName`), ADR-0019/0020 (two-layer extension shape), ADR-0024 (extension package pattern), ADR-0026 (enricher I/O boundary), ADR-0031 (`Devices.Displays()` enumeration sugar), ADR-0038 (Periphery-native backends over an unsuitable third-party hand-off), ADR-0052 (pure core / imperative shell precedent), ADR-0057 (Linux backend conventions)

> **Partially superseded by ADR-0059** (2026-06-12): topology concerns - position, primary, and the transactional CCD apply path - moved out of D8/NEG-005 into the `MonitorLayout` / `MonitorLayoutApplier` surfaces, driven by the fleet station agent's requirements. The per-monitor handle surface here is unchanged.

> **Number provisional.** Per this repo's convention the ADR number is assigned at merge; renumber if `0058` is taken by a parallel branch.

---

## Context

Periphery can *describe* a monitor end to end — `DeviceCategory.Monitor` enumerates on all three platforms, and the Windows enrichment stack (ADR-0013/0015/0018/0044) populates `MonitorName`, `DisplayResolution`, `DisplayBounds`, and connector metadata. What it cannot do is *talk to* one. Brightness, contrast, input source, and power state are runtime controls every physical monitor exposes over **DDC/CI** (the MCCS command set carried over the display's DDC I2C channel), and today the only options are vendor utilities or raw `ddcutil`/`dxva2` scripting outside the device model.

Concrete consumers:

- **The kiosk consumer's kiosks** — scheduled night dimming and burn-in mitigation for the always-on screens, and soft screen power-down (`VCP 0xD6`) without cutting AC or trusting panel auto-sleep. The kiosk already tracks its monitors through Periphery (`DeviceTracker` on `Category: Monitor`); control should hang off the same identity.
- **CLI / operator tooling** — `periphery monitor set-brightness 30` against a remote box over SSH is the monitor-shaped sibling of the existing `battery` verbs.
- **Workstation/demo rigs** — input-source switching (`VCP 0x60`) turns a dual-input monitor into a poor man's KVM.

### Why a new extension package

The core stays enumeration-only (ADR-0024); device communication lives in extensions behind the core's device identity (`Periphery.Camera`, `Periphery.Hid`, `Periphery.Usb`). Monitor control is exactly that shape: open a handle from a `DeviceInfo`, do I/O, dispose. The ADR-0026 boundary also applies — control-channel reads (a capabilities exchange takes tens of milliseconds *per monitor* with mandatory inter-command delays) must never ride enumeration.

### Why Periphery-native backends, again

The ADR-0038 hand-off test fails here too: `ddcutil`'s library is GPL-2 (unlinkable for our AOT story), the .NET DDC/CI libraries are abandoned one-platform wrappers, and the native surface we need is small — half a dozen calls per platform. No ecosystem is being alienated by building our own.

## Decision

### D1. The package is `Periphery.Monitor`

Extensions are named for the `DeviceCategory` whose devices they open: `Periphery.Hid` opens `Hid`, `Periphery.Camera` opens `Camera`. This package opens `DeviceCategory.Monitor` (the screen), so it is `Periphery.Monitor`.

Alternatives rejected: **`Periphery.Display`** — `DeviceCategory.Display` is the GPU/adapter, the wrong device; **`Periphery.Screens`** — aligns with no category and breaks the family rule. The vocabulary collision with device *monitoring* (`IDeviceMonitorProvider`, `DeviceWatcher`) is real but tolerable: the public types read unambiguously in context (`MonitorDevice` is a device; `DeviceWatcher` watches), and the category has been named `Monitor` since 1.0.

### D2. Two-layer shape, with Layer 2 earning its keep

Per the family pattern (ADR-0019/0020):

- **Layer 1 — `MonitorDevice.OpenAsync(DeviceInfo, CancellationToken)`**: one-shot handle; dead on unplug.
- **Layer 2 — `MonitorDeviceProxy.OpenAsync(DeviceProfile, IReconnectPolicy?, …)`**: reconnect-resilient.

Layer 2 matters *more* here than for HID/USB: monitors hot-unplug routinely, and DDC/CI goes unresponsive while a panel is off or asleep — a daily-night-dimming consumer lives through both constantly. Transient DDC failures while powered off must surface as a typed, retryable condition, not a poisoned handle.

### D3. The backend primitive is raw VCP + the capabilities string; semantics are shared and pure

```csharp
internal interface IMonitorBackend : IAsyncDisposable
{
    Task<VcpFeatureValue> GetVcpFeatureAsync(byte code, CancellationToken ct);   // current, maximum, type
    Task SetVcpFeatureAsync(byte code, ushort value, CancellationToken ct);
    Task<string> GetCapabilitiesStringAsync(CancellationToken ct);               // raw MCCS caps
}
```

Everything meaningful sits *above* the seam, shared by all platforms and pure (functional core / imperative shell, as in ADR-0052):

- **`MccsCapabilities.Parse(string)`** — the parenthesized MCCS capabilities grammar (`vcp(10 12 60(0F 11 12) …)`, `mccs_ver(2.2)`, `model(…)`) → supported codes, per-code value lists, version. Golden-vector unit tests from real monitor strings.
- **`VcpCode`** — the named constants v1 touches: `Luminance` 0x10, `Contrast` 0x12, `InputSource` 0x60, `PowerMode` 0xD6, `AudioVolume` 0x62.
- **Semantic helpers on `MonitorDevice`** — `GetBrightnessAsync()`/`SetBrightnessAsync(double percent)` normalize over the feature's reported maximum (panels disagree on max; percent is the honest unit), `SetPowerModeAsync(MonitorPowerMode)` maps the 0xD6 enum, `GetInputSourceAsync()`/`SetInputSourceAsync(…)` the 0x60 table.

Deliberate consequence: the **Windows backend uses only the low-level dxva2 VCP calls** (`GetVCPFeatureAndVCPFeatureReply`, `SetVCPFeature`, `CapabilitiesRequestAndCapabilitiesReply`) and *not* the high-level `GetMonitorBrightness` family — so normalization, caps interpretation, and quirks live in exactly one (pure, tested) place and Linux/Windows cannot drift.

### D4. Platform backends

**Windows — `Windows.DxvaMonitorBackend`.** Identity resolution: `DeviceInfo.Id` is the SetupAPI instance (`DISPLAY\GSM5BBF\…`); correlate it to a `DISPLAYCONFIG_TARGET_DEVICE_NAME.monitorDevicePath` (the same QueryDisplayConfig correlation the core's `WindowsDisplayConfigEnricher` performs), map the target's source to its `HMONITOR` via `EnumDisplayMonitors`/`GetMonitorInfo` device-name match, then `GetPhysicalMonitorsFromHMONITOR` for the `PHYSICAL_MONITOR` handle (index-correlated when one `HMONITOR` carries several targets — rare, documented). Self-contained in the extension, like `WinUsbBackend` owns its own cfgmgr32 resolution (see OQ-001).

**Linux — `Linux.I2cDdcMonitorBackend`.** Identity resolution follows ADR-0057 D2 verbatim: `DeviceInfo.Id` is the DRM connector syspath (`…/drm/card1/card1-HDMI-A-1`); its **`ddc` symlink** names the connector's I2C adapter → `/dev/i2c-N`, opened `O_RDWR | O_CLOEXEC` with `ioctl(I2C_SLAVE, 0x37)`. `/dev/i2c-*` strings pass through verbatim; unrecognized identities are `MonitorDeviceNotFoundException` (the ADR-0057 classification). The DDC/CI packet layer — source/length/checksum framing for Get/Set VCP and the chunked capabilities read — is a pure `DdcCiWire` codec with golden vectors. The mandatory inter-command spacing (≥ 40 ms per MCCS; `ddcutil` defaults to 50) is pure cadence state advanced by a shell-owned clock, per the functional-core convention — never a sleep buried in the codec. Requires a kernel new enough to expose the connector `ddc` link (mainline drivers have it since ~4.19); EDID-matching fallback across `/dev/i2c-*` is deferred until a target needs it. Access needs an `i2c` group/udev rule, documented like the hidraw/usbfs rules.

**macOS — deferred**, consistent with the rest of the family (and DDC on Apple Silicon means private `IOAVService` APIs; not worth the tax until a consumer exists).

### D5. v1 public surface sketch

```csharp
var screen = await Devices.Enumerate()
    .OfCategory(DeviceCategory.Monitor)
    .WithName("<the panel's EDID name>")
    .FirstOrDefaultAsync();

await using var monitor = await MonitorDevice.OpenAsync(screen!);

var caps = await monitor.GetCapabilitiesAsync();          // parsed MccsCapabilities
if (caps.Supports(VcpCode.Luminance))
    await monitor.SetBrightnessAsync(0.30);               // night dimming

await monitor.SetPowerModeAsync(MonitorPowerMode.Standby);

// Escape hatch — any VCP code, raw:
var raw = await monitor.GetVcpFeatureAsync(0x60);          // input source
await monitor.SetVcpFeatureAsync(0x60, 0x11);              // HDMI-1
```

CLI: `periphery monitor list` (caps summary per monitor), `periphery monitor caps <id>`, `periphery monitor set-brightness <pct> [--id …]`, `periphery monitor vcp get|set <code> [value]` — same Spectre rendering conventions as `battery`.

### D6. Testing: the first extension whose integration tier cannot run on the virtual rig

- **Pure tier (per-PR, all platforms):** `MccsCapabilities` parser goldens (real capability strings, malformed/truncated inputs), `DdcCiWire` encode/decode vectors with checksum corruption cases, brightness normalization, power/input mappings.
- **Device tier (env-gated `PERIPHERY_MONITOR_DEVICE_TESTS=1`, `Category=Integration`, hard-fail not skip — ADR-0057 discipline):** requires a *physical* monitor. QEMU's virtual GPUs expose no DDC channel, so **the Linux device rig cannot host this tier** — unlike camera/HID/USB there is no faithful kernel-level fake. Validation paths: any dev workstation for the Windows leg; for Linux, a box with a real display output and a monitor attached. This constraint is accepted, not worked around: a DDC emulator would validate our own assumptions, not monitor reality.

## Negative space

- **NEG-001 — Internal-panel backlight is out of v1.** Laptop/embedded panels without DDC/CI use a different mechanism entirely (`/sys/class/backlight`, WMI). The `IMonitorBackend` seam doesn't preclude a future backlight backend selected by device characteristics, but v1 is external DDC/CI monitors only.
- **NEG-002 — No change *watching*.** v1 has no events for externally-changed VCP values (user presses the OSD buttons); polling is the consumer's policy, mirroring ADR-0048 OQ-003's cadence stance.
- **NEG-003 — No public EDID parsing.** Core enrichment already surfaces the name/resolution metadata consumers need; a full EDID library is a different product.

## Open questions

- **OQ-001** — Should core export its Windows instance-ID ↔ DISPLAYCONFIG correlation for extensions, or does each extension keep self-containing its resolver (the `WinUsbBackend` precedent)? Start self-contained; revisit if a third copy appears.
- **OQ-002** — Do the target panels actually implement DDC/CI? **Probe real hardware before building** — if the panels reject VCP, the kiosk consumer shrinks to nothing and the priority drops to CLI/workstation tooling. This is the cheapest possible de-risk and should gate implementation.
- **OQ-003** — Monitor identity stability across reconnects for `DeviceProfile` matching (serial-bearing EDIDs vs the many panels shipping all-zero serials); likely answerable with the kiosk's existing `IdStartsWith` workaround until proven otherwise.

## Consequences

- The extension family stays symmetric: enumerate in core, open-by-identity in the extension, two layers, internal per-platform backends, pure protocol cores. macOS remains the uniform deferred column.
- A new privileged-access story to document (Linux `i2c` group), alongside the existing hidraw/usbfs/video notes.
- First extension with a hardware-gated integration tier — CI covers the pure tier everywhere, and the device tier becomes a documented manual/bench step until a bench box with a real monitor joins the runner fleet. *(Softened by the Amendment: the display-mode plane is VM-testable; only the DDC/VCP plane is hardware-gated.)*

---

## Amendment (2026-06-12): display-mode control — resolution, orientation, refresh

Same-day scope expansion, recorded as an amendment per the ADR-0010 convention. The original sections above are unchanged.

### Context: these are not monitor properties, and that matters

Brightness, contrast, input source, and power live *in the panel* and are controlled over the monitor's own DDC channel — that is the D1–D6 design. Resolution, orientation, and refresh rate live in the **OS display stack**: they are the mode programmed onto the GPU's output path that drives the panel. Different owner, different API surface, different failure domains — a virtual display has a fully functional mode-set path and no DDC channel at all, and a physical panel can refuse DDC while mode-setting works perfectly. Conflating the two behind one backend would re-fuse concerns the package shape exists to separate.

They nonetheless belong in *this* package: the consumer's unit of thought is "configure this screen" (the kiosk provisioning story is literally "make the POS panel 720x1280 portrait, then dim it at night"), the identity anchor is the same `DeviceCategory.Monitor` device, and core precedent already hangs resolution metadata (`DisplayResolution`, `DisplayBounds`) on the Monitor `DeviceInfo`, not the GPU's.

Concrete consumer, sharpened: the kiosk template runs a **720x1280 portrait primary**, configured today by a bespoke provisioning script. `periphery monitor set-orientation portrait` + `set-resolution 720x1280` from the install flow replaces that with fleet tooling.

### D7. A second seam: `IDisplayModeBackend`, composed by the same `MonitorDevice`

```csharp
internal interface IDisplayModeBackend : IAsyncDisposable
{
    Task<DisplayMode> GetCurrentModeAsync(CancellationToken ct);
    Task<IReadOnlyList<DisplayMode>> GetSupportedModesAsync(CancellationToken ct);
    Task SetModeAsync(DisplayMode mode, bool persist, CancellationToken ct);
    Task<MonitorOrientation> GetOrientationAsync(CancellationToken ct);
    Task SetOrientationAsync(MonitorOrientation orientation, bool persist, CancellationToken ct);
}
```

`MonitorDevice` owns up to two backends — VCP (D3) and display-mode — and each is **independently present**: `monitor.SupportsVcp` / `monitor.SupportsDisplayMode` report what this handle can actually do. A QEMU/IddSample virtual display opens with display-mode only; a desk monitor typically opens with both. Opening succeeds if *either* plane resolves; per-plane calls on an absent plane throw a typed `MonitorCapabilityException`.

`DisplayMode` is `(int Width, int Height, Rational RefreshRate)` (reusing the camera's `Rational` for fractional rates); `MonitorOrientation` is `Landscape / Portrait / LandscapeFlipped / PortraitFlipped`, with the semantic layer owning the landscape↔portrait **width/height swap** so consumers never hand-roll the classic DEVMODE rotation bug.

### D8. Windows display-mode backend: `ChangeDisplaySettingsEx` on the resolved source

The D4 identity correlation already yields the path's *source*; `DISPLAYCONFIG_SOURCE_DEVICE_NAME` gives its GDI device name (`\\.\DISPLAY1`), which is exactly what the mode-set API wants:

- Enumerate: `EnumDisplaySettingsExW(source, iModeNum, …)` loop; current mode via `ENUM_CURRENT_SETTINGS`.
- Set: `ChangeDisplaySettingsExW(source, ref DEVMODE, CDS_TEST)` probe first, then commit with `CDS_UPDATEREGISTRY` when `persist: true` (the kiosk default) or `0` for session-scoped.
- Orientation: `DEVMODE.dmDisplayOrientation` (`DMDO_*`), with the width/height swap applied in the shared layer (D7).

`SetDisplayConfig` (the modern topology API) is deliberately *not* v1: per-path mode and rotation on an active source is `ChangeDisplaySettingsEx`'s well-trodden ground, and topology management is out of scope (NEG-005). Revisit if duplicated-topology or per-path refresh limits bite.

Session constraint, named: display settings belong to the interactive session. The kiosk deployment already runs workloads in Session 1 (roam `interactive-session: true`), so the consumer story holds; a Session-0 service cannot mode-set the console display (OQ-004).

### D9. Linux display-mode is deferred — the ownership problem, not the ioctls

On Linux there is no single "set the mode" API; there is a *session owner*:

- **Bare KMS** (no compositor): atomic mode-setting on `/dev/dri/cardN` needs DRM master — exclusive, and held by whatever fullscreen app/framework is rendering (the realistic Linux-kiosk shape). An outside library cannot and should not wrestle for it. Rotation on bare KMS is a driver-dependent plane property, frequently absent for primary planes.
- **X11**: RandR owns modes and implements rotation in the server.
- **Wayland**: compositor-specific protocols (`wlr-output-management`, GNOME/KDE DBus) — no universal surface.

Any honest backend is therefore *per-session-model*, and no known consumer has a pinned Linux session model for displays yet. Deferred (NEG-004) with the seam ready; the first concrete consumer (likely KMS-direct, via DRM lease or in-process integration) picks which backend gets built. The VCP/DDC plane (D4) is unaffected — it works on Linux regardless of who owns the mode.

### Surface and CLI additions

```csharp
await using var monitor = await MonitorDevice.OpenAsync(screen!);

if (monitor.SupportsDisplayMode)
{
    var modes = await monitor.GetSupportedModesAsync();
    await monitor.SetModeAsync(new DisplayMode(720, 1280, new Rational(60, 1)), persist: true);
    await monitor.SetOrientationAsync(MonitorOrientation.Portrait, persist: true);
}
```

CLI: `periphery monitor modes [--id …]`, `periphery monitor set-resolution 720x1280[@60] [--no-persist]`, `periphery monitor set-orientation portrait|landscape|portrait-flipped|landscape-flipped`.

### Testing: the display-mode plane restores VM coverage

The D6 hardware gate applies **only to the VCP plane**. Mode enumeration, set-resolution round-trips, and orientation flips all work against virtual displays — a dual-display VM template (one 720x1280 portrait + one 1920x1080) is a ready-made integration target that even exercises the orientation/swap logic, and a self-hosted Windows runner can host the env-gated tier (`PERIPHERY_MONITOR_DEVICE_TESTS=1`) for it. Pure-tier additions: DEVMODE construction (orientation swap cases), mode parsing/normalization, persist-flag mapping.

### Negative space (amendment)

- **NEG-004 — Linux display-mode backends** deferred per D9: per-session-model backends (KMS-atomic, RandR) wait for a consumer with a pinned session model.
- **NEG-005 — No topology management.** Multi-monitor arrangement, duplicate/extend, primary selection, and HDR/color depth are `SetDisplayConfig` territory and out of scope; this package configures *one monitor's* output, not the desktop.

### Open questions (amendment)

- **OQ-004** — ~~Confirm `ChangeDisplaySettingsEx` behaviour from the kiosk's exact execution context~~ **Resolved (2026-06-12, implementation):** validated on a dual-display bench VM via an Interactive-principal scheduled task in the kiosk consumer autologon console session — mode round-trip (1920x1080 ↔ 720x1280@60) and DMDO rotation (with the width/height swap) both apply and restore cleanly on the IddSample indirect displays, with no cross-talk between the two monitors. The flip side is confirmed too: display paths are session-local, so a remote/RDP/service session cannot reach the console session's monitors; the resolver's not-found error now teaches this.
- **OQ-005** — Default `persist` semantics: the CLI defaults to persist (provisioning is the use case); should the library API default match, or stay explicit-only? Draft says explicit parameter, CLI opinionated.

---

## Amendment (2026-06-12, #2): family-conformance pass against the core canon

A full read of the extension-package canon — ADR-0024 (the pattern contract), ADR-0026 (enricher I/O boundary), ADR-0027 (`DeviceProxyBase`), ADR-0032/0033 (session host; facade since removed), ADR-0035 §Layer-2 (when a *session* model is warranted), ADR-0047/0051 (capability tags), and `ARCHITECTURE.md` §1 (guiding constraints) — confirmed the draft's structure and surfaced four contracts it left implicit. This amendment pins them so the implementation cannot drift. No prior decision is reversed.

### D10. Family conformance, stated explicitly

- **Layer 1.** `MonitorDevice` follows ADR-0024's Layer-1 rules verbatim: `sealed`, `IAsyncDisposable`, static `OpenAsync(DeviceInfo, …)` as the only public construction path, a `DeviceInfo` property set at construction and readable after disposal, no public constructors.
- **Layer 2 is a `DeviceProxyBase` derivation, not a hand-rolled loop.** `MonitorDeviceProxy` is `sealed : DeviceProxyBase<MonitorDevice, MonitorException>` (ADR-0027, as `UsbDeviceProxy` is today), inheriting the reconnect state machine, the awaitable init gate, `ConnectionState`/`GaveUp`, and the injectable `IReconnectPolicy` seam (ADR-0055). It ships both canonical factory shapes: `OpenAsync(DeviceProfile, IReconnectPolicy?, ct)` (owned tracker + watcher) and `Create(DeviceTracker, …)` (borrowed, shared-watcher — the kiosk already runs one watcher over several trackers).
- **Proxy, deliberately not a session.** ADR-0035 reserves the `CameraSession` / `DeviceSessionHost<TSession>` shape for *configured streaming runtimes* — a negotiated format with a producer lifecycle. Monitor control is one-shot control-plane I/O like HID and USB, so it takes the proxy shape; consumers who want session publication compose `DeviceSessionHost<TSession>` themselves per ADR-0032 (the facade layer was removed — ADR-0033 is superseded; nothing here should reintroduce one).
- **Star topology.** `Periphery.Monitor` depends on `Periphery` core only — no spoke-to-spoke references. A monitor that is also a USB device is handled the multi-aspect way (ADR-0024): the caller holds `MonitorDevice` and `UsbDevice` side by side off the same `DeviceInfo` passport.
- **AoT constraints.** All P/Invoke via `[LibraryImport]`; any native callback via `[UnmanagedCallersOnly]` with `GCHandle`-carried context (the ADR-0057 backends are the live template). The ADR-0024 two-zone ring buffer is explicitly **not** required here — its skip-condition applies (infrequent, non-timing-critical control-plane calls; nothing approaches 1 kHz).

### D11. Exception hierarchy follows the family's practice

`MonitorException : IOException`, with `MonitorAccessDeniedException`, `MonitorDeviceNotFoundException`, `MonitorDeviceLostException`, and `MonitorCapabilityException` (the D7 absent-plane error) as subtypes, all carrying the optional `DeviceId` and wrapping the platform error as the inner exception. Note for the record: ADR-0024's text says extension exceptions derive from `DeviceEnumerationException`, but every shipped extension (`HidException`, `UsbException`, `CameraException`, `TreehopperException`) derives from `IOException` — the practice is the convention; this package matches the practice, and ADR-0024 should be corrected to match reality rather than this package matching a dead letter.

### D12. ADR-0026 conformance: Option D helper in, enricher and capability tag out

- **In:** a static snapshot helper, `MonitorDevice.ReadCapabilitiesAsync(DeviceInfo, ct)` — transient open → MCCS capabilities + current mode → close — the same Option D shape as `HidBattery.ReadSnapshotAsync`. The CLI's `monitor list` (one summary row per monitor) is exactly this helper in a loop; without it the CLI would have to hand-roll transient handles.
- **Out:** there is deliberately **no** `MonitorEnricher` and **no** `DeviceTags.DdcCapable` capability tag. DDC/CI support is only knowable by opening the I2C/dxva channel and asking — device I/O — and ADR-0026's hard rule (Option A) forbids enrichers from performing I/O. This is the same boundary that makes `HidBatteryEnricher` classify by VID:PID quirks table instead of probing. A capability tag that required I/O to compute would be a lie at enumeration time; consumers discover DDC support at open (`SupportsVcp`) or via the Option D helper, both honestly priced.

### D13. Relationship to core's typed monitor properties (the promotion rule)

Core already owns the *enumeration-time, zero-I/O* monitor metadata as typed `DeviceInfo` properties — `MonitorName` (ADR-0018/0044), `DisplayResolution`, `DisplayBounds`, connector kind. This package does not duplicate, populate, or bypass them: they remain the snapshot view, while `MonitorDevice.GetCurrentModeAsync` / `GetVcpFeatureAsync` are the *live, handle-gated* reads (a mode can change after enumeration; a brightness value only exists behind the handle). v1 adds **no** new `DeviceInfo` properties and touches no `Properties` bag. If OQ-003's identity work surfaces a scalar enumeration-time datum worth keeping (e.g. EDID serial), it goes to core as a typed nullable property via the ADR-0024 promotion rule — not into the bag, not into this package.

### Ethos check: platform parity at the abstraction layer

`ARCHITECTURE.md` §1 requires categories to be meaningfully supportable across platforms while *providers ship incrementally* — the exact trajectory Camera/Hid/Usb just completed (Windows-first, Linux in ADR-0057, macOS pending). This package follows it: both seams (`IMonitorBackend`, `IDisplayModeBackend`) are platform-neutral abstractions; the VCP plane is cross-platform from day one, the display-mode plane ships Windows-first with the Linux path named and deferred for a reason (D9), and nothing in the public surface encodes a platform.

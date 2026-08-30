---
title: "ADR-0038: Periphery.Usb — Periphery-native raw USB I/O via per-platform backends"
status: "Accepted"
status_note: "Shipped - `src/Periphery.Usb`, WinUSB (Windows) and libusb (Linux) backends."
date: "2026-05-08"
authors: "@charles8051"
tags: ["architecture", "decision", "usb", "extension", "interop", "winusb", "libusb"]
supersedes: ""
superseded_by: ""
---

# ADR-0038: Periphery.Usb — Periphery-native raw USB I/O via per-platform backends

## Context

Periphery core already enumerates USB devices through the per-platform
providers (`WindowsDeviceMonitorProvider` via SetupAPI,
`LinuxDeviceProvider` via udev, `MacOSDeviceProvider` via IOKit). What
it doesn't do is hand the user a *handle* — descriptors beyond what
enumeration captures, claim an interface, run control / bulk /
interrupt / isochronous transfers.

There's a real audience for that:

- **Custom-protocol devices** — lab instruments (Saleae, Picoscope),
  programmers (J-Link, ST-Link, Bus Pirate), dev boards, hobby
  gadgets with vendor-specific bulk endpoints. The
  [Treehopper SDK rebuild](../plans/periphery-treehopper.md) is the
  immediate motivating consumer.
- **Generic USB tooling** — descriptor inspection, mass-claim test
  rigs, devices that don't fit any standard class.

The other extensions Periphery already plans (HID, Camera, Serial,
Audio) sit on class-specific stacks. Generic raw USB is the slice
none of those cover.

### Why not hand off to LibUsbDotNet

An earlier draft of this ADR proposed handing off
[LibUsbDotNet][libusb-dotnet]'s `UsbDevice` to consumers, following
the same "discover + hand off to a mature third-party library" pattern
Periphery uses for camera (and plans for audio / serial). On
investigation that pattern doesn't fit raw USB:

1. **License.** LibUsbDotNet ships under **LGPL-3.0**. The native
   `libusb-1.0` underneath is LGPL-2.1 *with a static-linking
   exception*, but LibUsbDotNet itself doesn't carry that exception.
   Under NativeAOT (which statically links), the LGPL terms apply
   to the whole binary. Periphery aims to be AOT-friendly per
   ADR-0035 and the trim/AOT story across the rest of the project,
   so this is a real constraint, not a paperwork one.
2. **Release cadence.** As of May 2026 the latest release is a
   prerelease tag `snapshot_22Sept2023` — over two years of unreleased
   commits. The repo is actively committed to (master is current),
   but consumers either pin a 2.5-year-old prerelease or build their
   own snapshot. Not a posture we want for a foundation extension.
3. **No real ecosystem to cut users off from.** The "hand off"
   principle works for camera (Vortice), audio (NAudio), serial
   (`System.IO.Ports`) because each has a real consumer ecosystem
   with its own docs and Stack Overflow answers. **Raw USB in
   .NET doesn't.** LibUsbDotNet *is* the one cross-platform option,
   and it has the issues above. There's no ecosystem we'd be
   alienating by exposing our own surface.
4. **Surface size.** What we actually need from raw USB is small —
   ~12 native functions (init, list/free, descriptor walk, open/close,
   claim/release, control + bulk + interrupt transfers, hot-plug
   callback). LibUsbDotNet's broader surface isn't paying for itself
   for our consumers.

The hand-off principle is a heuristic, not a rule. It applies when
there's a strong third-party ecosystem worth deferring to. For raw
USB there isn't, so the principle doesn't fit and we shouldn't
force it.

[libusb-dotnet]: https://github.com/LibUsbDotNet/LibUsbDotNet

## Decision

Build `Periphery.Usb` with **Periphery-native I/O on per-platform
backends**:

1. **Public API** — a small, Periphery-shaped `UsbDeviceProxy` that
   owns the lifecycle and exposes descriptor metadata + transfer
   methods directly. Consumers don't see WinUSB or libusb; they see
   `proxy.BulkReadAsync(...)`, `proxy.BulkWriteAsync(...)`,
   `proxy.ControlTransferAsync(...)`, etc.

2. **Windows backend — WinUSB direct.** `[LibraryImport("winusb.dll")]`
   plus a small SetupAPI shim only for the bits not already covered by
   `WindowsDeviceMonitorProvider`. **No native binary to ship**
   (WinUSB ships in Windows). AOT-clean. ~150-200 lines of interop.

3. **Linux/macOS backend — `libusb-1.0` direct.** Shared between the
   two platforms. `[LibraryImport("libusb-1.0.so.0")]` /
   `[LibraryImport("libusb-1.0.0.dylib")]` via per-platform module
   resolution. `libusb-1.0`'s own LGPL-2.1-with-linking-exception is
   AOT-clean, and we link against it as a system / shipped native
   library, not a managed wrapper. ~150-250 lines of interop.

4. **Reconnect resilience** — through `DeviceSessionHost<UsbSession>`,
   same as Periphery.Camera. No new lifecycle machinery.

5. **Driver-binding metadata** — surface "is this device bound to a
   claimable driver on this platform?" so users find out before
   opening, not via an opaque error. Ship v1 without this; add as a
   follow-up enricher once Periphery.Treehopper has flushed out the
   real-world cases.

```csharp
// Discovery — already in core
var devices = await Devices.Enumerate()
    .OfCategory(DeviceCategory.UsbDevice)
    .WithUsbId("10C4", "8A7E")
    .ToListAsync();

// One-shot open (Periphery-native types throughout)
await using var proxy = await UsbDeviceProxy.OpenAsync(devices[0], ct);
proxy.ClaimInterface(0);
var rx = await proxy.BulkReadAsync(endpointAddress: 0x81, count: 64, ct);
await proxy.BulkWriteAsync(endpointAddress: 0x01, data, ct);

// Reconnect-aware
var profile = new DeviceProfile(f => f.WithUsbId("10C4", "8A7E"), "MyDevice");
await using var host = await DeviceSessionHost<UsbSession>.StartAsync(
    profile,
    createSession: (info, ct) => UsbSession.OpenAsync(info, ct),
    ct: ct);
```

## Rationale

- **License cleanliness end-to-end.** Periphery is MIT.
  `libusb-1.0`'s LGPL-2.1-with-linking-exception is AOT-clean for
  static linking. WinUSB is part of Windows and license-free for
  use. No LGPL ripple through our binary.
- **AOT-friendly by construction.** All interop via
  `[LibraryImport]` source-generators. No reflection in the hot path.
- **Predictable update cadence.** We control the C# code; the only
  external moving parts are libusb releases (which are well-managed
  upstream) and Windows itself.
- **Smaller native surface to ship.** WinUSB is in-OS. libusb is a
  system package on every modern Linux distro and ubiquitous on
  macOS via Homebrew. We can ship `libusb-1.0` binaries via NuGet
  `runtimes/<rid>/native/` for users who want zero-system-deps, but
  the default path is fine without that.
- **Surface area is genuinely small.** Hand-rolling raw USB is a
  ~600-1000 line project once you account for both backends, the
  shared `IUsbBackend` shim, and the public `UsbDeviceProxy`. That's
  not free, but it's not the 1500-line-per-platform nightmare the
  original Treehopper SDK landed in (which we'd be replacing
  anyway).
- **Foundation for future custom-USB extensions.**
  Periphery.Treehopper (ADR-0039), Periphery.Saleae,
  Periphery.JLink, etc. all sit on this. Worth doing once,
  cleanly, even if the immediate audience is small.

## Alternatives considered

- **Hand off to LibUsbDotNet (the original ADR-0038 draft).**
  Rejected — see the "Why not hand off to LibUsbDotNet" section
  above. LGPL-3.0 ripple under AOT, no recent stable release, and
  the hand-off principle's premise (defer to a strong third-party
  ecosystem) doesn't apply to raw USB the way it does to audio,
  video, or serial.

- **Use `libusb-1.0` on all three platforms (uniform backend).**
  Plausible. Pro: single codebase, no Windows-specific WinUSB code.
  Con: forces users to install libusb on Windows (Zadig / `.inf`
  rebinding), which they already need for *some* devices but not
  all class-driver-bound ones. Going WinUSB-direct on Windows means
  Treehopper users with the standard `.inf` install just work. We
  prefer that ergonomically and the extra interop cost is modest.

- **Use IOKit USB Family directly on macOS** instead of libusb.
  Eliminates the macOS native dep entirely. ~500-700 lines of
  additional interop vs. ~150 for libusb. ROI not great in v1; can
  revisit if anyone hits problems with the libusb path on macOS.

- **Skip Periphery.Usb; let each consumer (Treehopper etc.) take
  the USB dependency directly.** Rejected — duplicates descriptor
  enrichment, hot-plug bridging, and reconnect plumbing across
  every consumer. Foundation extension pays for itself with two
  consumers.

## Consequences

### What we gain

- A working raw-USB story across Windows / Linux / macOS in one
  package, license-clean and AOT-friendly.
- A Periphery-shaped public API designed for our consumers'
  scenarios (async + cancellation throughout, `IAsyncDisposable`,
  identity via `DeviceInfo`).
- Foundation extension that future custom-USB consumers
  (Treehopper, Saleae, J-Link, …) sit on without each
  re-implementing the bridge to whatever USB library was popular
  the year they were built.

### What we accept

- **More code in our repo** vs. taking a third-party dependency.
  Estimate ~600-1000 lines total for both backends + shared
  surface + descriptor walking. Tractable.
- **Two interop surfaces to maintain** (WinUSB on Windows, libusb
  on Linux/macOS). Mitigated by aggressive sharing through
  `IUsbBackend`; only the platform-specific bits live in the
  per-platform backends.
- **libusb native binary distribution** on Linux/macOS. Most
  systems have it; for the rest we ship `libusb-1.0` per RID via
  NuGet `runtimes/`. Adds CI work and CVE-tracking for the shipped
  binaries. Not nothing, not a deal-breaker.
- **A deliberate departure from the hand-off principle.** ADR-0035
  established the discover-and-hand-off pattern for camera. ADR-0038
  is the first explicit case where that pattern doesn't fit and we
  own the I/O surface ourselves. Future extension ADRs should
  evaluate against both options rather than defaulting either way.

### What we constrain

- **Public API surface** stays narrow and Periphery-shaped. No
  copies of LibUsbDotNet's API. No exposed `IntPtr`s for
  `libusb_device_handle*`. Consumers see `UsbDeviceProxy` and
  descriptor types and async transfer methods.
- **Wire-level USB semantics** stay accurate to the spec — we
  don't invent new transfer types or sequences; we just expose the
  ones from USB 2.0 / 3.x in a typed C# surface.

## Affected files (planned)

```
src/Periphery.Usb/
├── Periphery.Usb.csproj
├── UsbDeviceProxy.cs                    # public: open / claim / transfers
├── UsbInterfaceDescriptor.cs            # POCO descriptor types
├── UsbEndpointDescriptor.cs
├── UsbConfigurationDescriptor.cs
├── UsbTransferDirection.cs
├── UsbTransferType.cs
├── UsbException.cs
├── Internal/
│   ├── IUsbBackend.cs                   # platform shim
│   ├── UsbBackendFactory.cs             # OS dispatch
│   ├── Windows/
│   │   ├── WinUsbBackend.cs             # claim + transfers via WinUSB
│   │   └── WinUsbInterop.cs             # ~150-200 lines [LibraryImport]
│   └── Posix/
│       ├── LibUsbBackend.cs             # claim + transfers via libusb
│       └── LibUsbInterop.cs             # ~150-250 lines [LibraryImport]
```

Plus, in core, descriptor enrichment if not already complete:

```
src/Periphery/Windows/WindowsUsbDescriptorEnricher.cs    # (if needed)
src/Periphery/Linux/LinuxUsbDescriptorEnricher.cs        # (if needed)
```

Examples + tests:

```
examples/Periphery.Usb.Example/         # list / dump descriptors / claim / read
tests/Periphery.Usb.Tests/              # fake IUsbBackend round-trips
docs/patterns/usb-device-handoff.md     # how downstream extensions consume
```

(The pattern doc filename keeps "hand-off" terminology even though
the underlying I/O is ours — from a consumer's perspective the
shape is still "core gives you a `DeviceInfo`, we give you a usable
device handle." We just don't pretend that the handle came from
somebody else's library.)

## Implementation order

See [`docs/plans/periphery-treehopper.md`](../plans/periphery-treehopper.md)
§ "Implementation order" (step 1). Estimate revised upward modestly
to **2–3 weeks** for Periphery.Usb v1 (vs. 1–2 in the LibUsbDotNet
draft) to account for the extra interop work. The downstream Treehopper
work (ADR-0039) is unchanged.

## Open questions

1. **WinUSB hot-plug on non-class devices.** Periphery core already
   gets device-arrival events via SetupAPI. Worth a smoke test that
   the path lights up cleanly for WinUSB-bound devices like
   Treehopper specifically.
2. **libusb async transfer model.** libusb async requires either a
   dedicated event-handling thread (`libusb_handle_events_completed`
   in a loop) or platform poll-fd notifiers. Pick the former for
   v1 (simpler, well-understood); revisit if that thread shows up
   as a profiling hotspot.
3. **macOS arm64 libusb binary.** Homebrew has it. NuGet
   `runtimes/osx-arm64/native/libusb-1.0.0.dylib` packaging needs
   verification.
4. **Driver-binding metadata.** Defer to a follow-up enricher once
   we see real cases through Periphery.Treehopper.

## Related ADRs

- [ADR-0035 — Periphery.Camera](0035-periphery-camera.md) — the
  hand-off principle this ADR explicitly diverges from for raw USB.
- [ADR-0039 — Periphery.Treehopper](0039-periphery-treehopper.md) —
  the immediate motivating consumer.

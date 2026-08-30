---
title: "ADR-0057: Linux backends for the I/O extensions — hidraw, libusb-1.0, V4L2"
status: "Accepted"
date: "2026-06-12"
authors: "@charles8051 (design + implementation)"
tags: ["architecture", "decision"]
supersedes: ""
superseded_by: ""
---

# ADR-0057: Linux backends for the I/O extensions — hidraw, libusb-1.0, V4L2

**Tracks:** `Periphery.Hid.Linux.LinuxHidBackend`, `Periphery.Hid.HidReportDescriptor`, `Periphery.Usb.Linux.LibUsbBackend`, `Periphery.Camera.Linux.V4l2CameraBackend`, `Periphery.Linux.LinuxDeviceProvider` (HID_ID / name / bus-type parity)
**Related:** ADR-0010 (udev Linux core provider), ADR-0020 (HID extension; sketched the hidraw plan), ADR-0035 (camera foundation; named V4L2), ADR-0038 (USB extension; named libusb and rejected LibUsbDotNet), ADR-0052 (Treehopper pure core — rides the USB seam unchanged)

> **Number provisional.** Per this repo's convention the ADR number is assigned at merge; renumber if `0057` is taken by a parallel branch.

---

## Context

The core enumeration library has been cross-platform since ADR-0010/0011, but every I/O extension — `Periphery.Hid`, `Periphery.Usb`, `Periphery.Camera` — had exactly one backend behind its internal seam (`IHidBackend`, `IUsbBackend`, `ICameraBackend`), and it was the Windows one. Non-Windows callers hit `PlatformNotSupportedException` at the dispatch point. The seams were designed for this moment (ADR-0020 §"Linux", ADR-0035 §1a, ADR-0038 "per-platform backends"); this ADR records how the Linux side was actually filled in and the cross-cutting choices the three backends share.

## Decisions

### 1. Kernel ABI directly for HID and camera; libusb-1.0 for USB

`LinuxHidBackend` and `V4l2CameraBackend` P/Invoke the stable kernel ABIs (`/dev/hidrawN` + `HIDIOC*` ioctls; `/dev/videoN` + `VIDIOC_*` ioctls) with zero library dependencies. `LibUsbBackend` depends on `libusb-1.0.so.0` instead of raw usbfs, because usbfs's asynchronous URB surface (submit/reap/discard, transfer chunking, zero-length-packet rules) is exactly the subtle machinery libusb exists to get right, and ADR-0038 already committed to libusb. The core provider set the dependency precedent (`libudev.so.1`); libusb-1.0 is packaged everywhere libudev is.

### 2. One open path: enumeration identity → sysfs walk → device node

`DeviceInfo.Id` on Linux is the udev syspath. Each backend resolves it to its openable node by walking sysfs — no libudev round-trip, no global device-list scan:

| Backend | Resolution |
|---|---|
| HID | ascend from the syspath until a `hidraw/` class directory appears → `/dev/hidrawN` (covers `hid` and `input`/`event` enumeration shapes) |
| USB | ascend until `busnum`/`devnum` attributes appear → `/dev/bus/usb/BBB/DDD`, opened by Periphery and wrapped with `libusb_wrap_sys_device` (≥ 1.0.23) |
| Camera | syspath basename (`videoN`), else `video4linux/` class scan, else uevent `DEVNAME` → `/dev/videoN` |

`/dev/...` strings pass through verbatim so consumer-constructed paths keep working, mirroring the Windows backends' `\\?\` pass-through. Unrecognized identities throw `*DeviceNotFoundException` — the same classification Windows surfaces when an unresolvable ID falls through to `CreateFile`.

### 3. Cancellation: poll(2) + eventfd wake for fd backends; `libusb_cancel_transfer` for USB

The hidraw and V4L2 fds are opened `O_NONBLOCK`; blocking waits are `poll(2)` over `{device fd, per-backend eventfd}`, and cancellation/disposal signal the eventfd to wake the wait immediately. libusb transfers use the async API — each transfer completes on a per-backend event-pump thread, and a `CancellationToken` registration calls `libusb_cancel_transfer` (cancel-after-completion is a benign `NOT_FOUND`; the native transfer is freed only after the registration quiesces). Both wake paths are integration-tested with latency assertions.

### 4. Threading mirrors the Windows backends, not an idealized model

HID I/O and camera control ioctls run under `Task.Run` (the posture Windows feature reports already take). `V4l2CameraBackend.ReadRawFrameAsync` runs synchronously on the caller's thread for the same documented reason as the MF backend: the `CameraSession` producer task is LongRunning and owns a dedicated thread. The USB event pump is the IOCP analogue: completion-driven, one thread per open device, torn down via `libusb_interrupt_event_handler`.

### 5. HID framing differences are absorbed in the backend, not the surface

Windows' HID stack always reserves byte 0 for the report ID; hidraw does not: `read(2)` prepends the ID only for devices that use numbered reports, `write(2)` always takes a leading report-number byte (0 = unnumbered, stripped by the kernel), and the feature ioctls match Windows framing exactly. Whether reports are numbered comes from the report descriptor, which Linux must parse itself (`HIDIOCGRDESC`) — a new pure parser (`HidReportDescriptor`) recovers the application usage page/usage, numbered-report flag, and per-type maximum payload sizes that `HidP_GetCaps` provides on Windows. Full descriptor modelling stays out of scope (ADR-0020 NEG-004).

### 6. V4L2 frames are zero-copy with deferred requeue

`RawCameraFrame`'s contract — backing memory valid only until the next `ReadRawFrameAsync` — is exactly V4L2's mmap queue shape. The backend wraps the dequeued buffer's mapping directly (a `MemoryManager<byte>` per mmap) and re-queues it at the *next* read, after the frame pool has copied out. Stepwise/continuous frame-size descriptors (virtual drivers like v4l2loopback) synthesize the common resolution ladder clamped to the advertised range.

### 7. Core enumeration parity fixes ride along

Three Linux-provider gaps surfaced by the backends, fixed in core: `HID_ID` parsing (the only VID/PID identity a non-USB HID device has; unblocks `WithUsbId`, `HidBatteryEnricher`, and `HidQuirks` lookup), friendly-name fallbacks (`name` sysattr for v4l2/input class devices, `HID_NAME` for hid devices), and the `/devices/virtual/` → `BusType.Software` override now applying only when no concrete bus was inferred (a uhid device still lives on the HID bus, exactly as Windows reports virtual HID devices).

### 8. ABI posture: 64-bit glibc Linux

Struct layouts and ioctl numbers are the 64-bit generic-ABI values (`linux-x64` / `linux-arm64`): `v4l2_buffer`'s embedded `timeval` is two native words, so 32-bit ARM would need different offsets. Sonames are glibc-style (`libc.so.6`, `libusb-1.0.so.0`), matching the core provider's `libudev.so.1` posture; musl (Alpine) is out of scope until something needs it.

## Verification

A dedicated Linux VM — the device rig — provides reproducible virtual devices: v4l2loopback fed an ffmpeg test pattern, a `/dev/uhid` harness impersonating the Megatec `0665:5161` UPS (real Q1 protocol responses, feature reports), and QEMU-emulated USB HID devices on an emulated xHCI bus. Env-gated integration tests (`PERIPHERY_LINUX_DEVICE_TESTS=1`, `Category=Integration`) cover capture, the full battery-snapshot codec stack, USB descriptors/control transfers, and both cancellation wake paths; they run in CI on the rig itself (`.github/workflows/linux-ci.yml`). The rig deliberately hard-fails when a virtual device is missing rather than skipping.

## Consequences

- Treehopper works on Linux with no changes — its shell rides `IUsbBackend` (ADR-0052). Real-hardware validation of bulk endpoints on Linux remains open (the rig's emulated devices have no bulk endpoints); tracked separately.
- `HidBattery` and the CLI battery commands are `[SupportedOSPlatform]`-widened to Linux; `periphery battery list` produces the same live-snapshot table as Windows.
- macOS is now the only seam without a backend; the dispatch-point exceptions name it explicitly.
- Linux release *binaries* (publish.yml `release-binaries`) are deliberately deferred — packaging design (tar.gz layout, installer story) is a separate work item.

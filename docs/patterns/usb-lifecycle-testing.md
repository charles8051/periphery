# USB lifecycle testing for Periphery extensions

> **When to read this:** you're testing reconnect resilience, hot-plug
> handling, mid-transfer disconnect behavior, or anything else that
> involves a USB device coming and going. Either for a Periphery
> extension you're building or for an integration test against a real
> device.

The pattern lets you disconnect and reconnect a real USB device
**programmatically, without touching the cable**, with the same OS
events the kernel produces for a real cable yank. Linux, Windows
(via WSL2), VMs, and smart hubs are all viable hosts.

This is the lifecycle-testing companion to `FakeUsbBackend`-style
unit tests. Unit tests cover protocol and ergonomics; this pattern
covers the OS-stack interactions that fakes can't see.

---

## Why fakes aren't enough

[ADR-0035] / [ADR-0037] taught us the lesson the hard way on Periphery.
Camera: a fake-backend test suite passed at 79/79 while every real
camera failed because the fake never exercised the real interop
surface. USB has the same trap, made worse by hot-plug:

- Hot-plug events come from the OS (udev on Linux, SetupAPI / WM_DEVICECHANGE
  on Windows, IOKit on macOS). A fake backend can simulate the
  Periphery-internal effects but can't validate that we're reading
  the OS events correctly.
- Mid-transfer disconnect, claim races, descriptor re-fetch on
  replug — these all require a real kernel USB stack interacting
  with a real device.
- The original Treehopper SDK's reconnect bugs were specifically of
  this shape: the C# logic looked fine in isolation, but the kernel
  wasn't producing the events the SDK assumed.

[ADR-0035]: ../adr/0035-periphery-camera.md
[ADR-0037]: ../adr/0037-mf-sample-raw-vtable.md

---

## The mechanisms

Four ways to make a real USB device disappear and reappear without
touching it. Pick one based on your dev environment.

### 1. Linux `/sys/bus/usb/devices/<id>/authorized`

The cheapest, simplest, and most underrated. Linux exposes an
`authorized` flag on every USB device; writing `0` causes the kernel
to emit a real disconnect event and tear down the device. Writing
`1` re-enumerates it.

```bash
# Find your device's sysfs node by VID:PID
DEV=$(grep -lE '^10c4$' /sys/bus/usb/devices/*/idVendor 2>/dev/null \
      | xargs -I{} dirname {} \
      | xargs -I{} sh -c 'grep -l "8a7e" {}/idProduct && echo {}' \
      | head -1)
echo "Device sysfs: $DEV"

# Disconnect (real kernel remove event)
echo 0 | sudo tee "$DEV/authorized"

# Reconnect (real kernel add event)
echo 1 | sudo tee "$DEV/authorized"
```

**What it produces:** real udev `remove` and `add` events. The same
events libusb's hot-plug callback fires on. Periphery's
`LinuxDeviceProvider` reads them through the same path it reads cable
events.

**Fidelity:** very high. The kernel really thinks the device went
away. From the guest's perspective there is no observable difference
between this and a physical unplug.

**Limitations:** Linux only. Needs sudo or a udev rule (recipe below)
for unprivileged scripting.

### 2. WSL2 + `usbipd-win`

Microsoft's [usbipd-win] exposes Windows USB devices to WSL2 via the
`usbip` protocol. From inside WSL2 you `attach` and `detach` the
device on demand:

```powershell
# On the Windows host (one-time, as Administrator):
usbipd list
usbipd bind --busid 2-3                    # mark device shareable
```

```bash
# Inside WSL2:
sudo usbip attach -r <host_ip> -b 2-3      # connect
sudo usbip detach -p 0                     # disconnect
```

**What it produces:** real Linux-side USB add/remove events inside
WSL2, driven by the Windows host's `usbipd`.

**Fidelity:** high for the WSL2 side. The Windows host doesn't see
a disconnect — the device stays bound to `usbipd-win` — so this is
specifically for testing Periphery's libusb backend on Windows users'
machines.

**Limitations:** doesn't help with native Windows testing (the WinUSB
backend), since the device is bound to `usbipd-win` while attached
to WSL.

[usbipd-win]: https://github.com/dorssel/usbipd-win

### 3. QEMU/KVM USB passthrough + QMP

When you're developing inside a VM (Linux or Windows guest), QEMU's
QMP socket gives you scriptable hot-add and hot-remove of passthrough
USB devices. The host has the cable plugged in physically; the VM
sees the device come and go.

In the QEMU command line, expose a QMP socket:

```bash
qemu-system-x86_64 \
    -qmp unix:/tmp/qmp-treehopper,server,nowait \
    -usb \
    ... # rest of VM config
```

Then drive it from a script:

```bash
# Disconnect
echo '{ "execute": "qmp_capabilities" }
{ "execute": "device_del", "arguments": {"id": "treehopper"} }' \
| ncat -U /tmp/qmp-treehopper

# Reconnect
echo '{ "execute": "qmp_capabilities" }
{ "execute": "device_add",
  "arguments": {
    "driver": "usb-host", "id": "treehopper",
    "vendorid": "0x10C4", "productid": "0x8A7E"
  }
}' | ncat -U /tmp/qmp-treehopper
```

**What it produces:** real OS-level USB add/remove events inside the
guest. The guest's USB stack — Windows or Linux — handles them as
ordinary device events.

**Fidelity:** high inside the guest.

**Limitations:** requires a VM dev environment. The host briefly sees
the device disappear too (passthrough release), so don't run this
while another host process is talking to the device.

**Cross-OS bonus:** this is the cleanest path for testing the WinUSB
backend on a Linux dev box. Boot a Windows guest, pass the Treehopper
through, drive lifecycle from QMP.

### 4. `uhubctl` + a smart USB hub

For physical-level dropout testing — actual 5V power cuts and
re-enumeration — a USB hub with per-port power control is the gold
standard. [uhubctl] supports the chipsets used in many hubs (popular
ones include the Plugable USB 3.0 hub and the YEPKIT YKUSH).

```bash
# List ports
uhubctl

# Cut power to port 3 (real disconnect, 5V drops)
sudo uhubctl -p 3 -a off

# Restore power (full re-enumeration on the device side)
sudo uhubctl -p 3 -a on
```

**What it produces:** identical to a physical unplug. The device
itself reboots its USB stack; the host sees a remove + add cycle
including all enumeration overhead.

**Fidelity:** maximum. Catches things the OS-level mechanisms can't:
power-glitch reconnects, BIOS/firmware enumeration races, devices
that need a few hundred ms to settle.

**Limitations:** needs supported hardware (~$30 hub investment).
Slower than the OS-level options (~1–2 seconds per cycle vs <1).

[uhubctl]: https://github.com/mvp/uhubctl

---

## The `ILifecycleHarness` abstraction

Tests shouldn't care which mechanism is in play. Wrap them behind an
interface that lives in the integration-test project:

```csharp
namespace Periphery.Usb.IntegrationTests;

/// <summary>
/// Disconnects and reconnects a USB device under test without
/// physical action. Selected at test startup based on
/// PERIPHERY_LIFECYCLE_HARNESS env var or auto-detection.
/// </summary>
public interface ILifecycleHarness : IAsyncDisposable
{
    /// <summary>Identifies the device this harness controls.</summary>
    DeviceInfo Device { get; }

    /// <summary>
    /// Causes the OS to see the device as disconnected. Returns when
    /// the kernel-level remove event has been emitted (best effort).
    /// </summary>
    Task SimulateUnplugAsync(CancellationToken ct = default);

    /// <summary>
    /// Reconnects the device. Returns when the kernel-level add
    /// event has been emitted; the caller is responsible for
    /// awaiting any Periphery-level reconnect-resilient state.
    /// </summary>
    Task SimulateReplugAsync(CancellationToken ct = default);
}
```

Concrete implementations:

| Class                       | Mechanism                                  | Notes                                       |
|---|---|---|
| `LinuxAuthorizedHarness`    | sysfs `authorized` toggle                  | Default on Linux. No deps.                  |
| `WslUsbipdHarness`          | `usbip attach/detach` from WSL2            | Default on WSL2.                            |
| `QemuQmpHarness`            | QMP `device_add` / `device_del`            | When `PERIPHERY_QMP_SOCKET` is set.         |
| `UhubctlHarness`            | `uhubctl -p N -a off/on`                   | When `PERIPHERY_UHUBCTL_PORT` is set.       |

A simple selector picks at test startup:

```csharp
public static class LifecycleHarnessFactory
{
    public static async Task<ILifecycleHarness> CreateAsync(
        DeviceInfo device, CancellationToken ct = default)
    {
        var override_ = Environment.GetEnvironmentVariable("PERIPHERY_LIFECYCLE_HARNESS");
        return override_?.ToLowerInvariant() switch
        {
            "uhubctl" => await UhubctlHarness.AttachAsync(device, ct),
            "qmp"     => await QemuQmpHarness.AttachAsync(device, ct),
            "wsl"     => await WslUsbipdHarness.AttachAsync(device, ct),
            "linux"   => await LinuxAuthorizedHarness.AttachAsync(device, ct),
            null      => await AutoDetectAsync(device, ct),
            _ => throw new ArgumentException($"Unknown harness: {override_}"),
        };
    }
}
```

Tests look the same regardless of mechanism:

```csharp
[Fact]
[Trait("Category", "RequiresRealUsb")]
public async Task SessionHost_RecoversFromMidCaptureDisconnect()
{
    using var ct = TestCancellation();
    var device = await ResolveTreehopperAsync(ct);
    await using var harness = await LifecycleHarnessFactory.CreateAsync(device, ct);

    await using var host = await DeviceSessionHost<TreehopperBoard>.StartAsync(
        new DeviceProfile(f => f.WithUsbId(TreehopperBoard.Vid, TreehopperBoard.Pid)),
        TreehopperBoard.OpenAsync, ct: ct);

    await WaitForStatusAsync<SessionActive<TreehopperBoard>>(host, ct);

    await harness.SimulateUnplugAsync(ct);
    await WaitForStatusAsync<DeviceAbsent<TreehopperBoard>>(host, ct);

    await harness.SimulateReplugAsync(ct);
    await WaitForStatusAsync<SessionActive<TreehopperBoard>>(host, ct);
}
```

---

## Test scenarios this enables

Real OS events + real device + scriptable lifecycle = the following
test classes become tractable, all of which were impractical with
the original Treehopper SDK and remain impractical with `FakeUsbBackend`
alone:

- **Cold-plug / cold-unplug.** Open before plug, plug, observe
  `DeviceSessionHost` transitions; reverse.
- **Mid-transfer disconnect.** Start a long transfer (or hold the
  bulk-OUT endpoint busy with a soft-PWM stream), call
  `SimulateUnplugAsync`, assert the in-flight `Task` faults with the
  expected typed exception (`TreehopperDeviceLostException` or
  similar).
- **Reconnect identity.** Replug the same device — same serial,
  possibly different USB topology (different hub port). Assert
  Periphery's identity-by-`HardwareId` matches and `DeviceSessionHost`
  reuses the existing session slot.
- **Multi-board safety.** Two Treehoppers connected, disconnect one,
  assert the other's session is unaffected.
- **Rapid-cycle replug.** Five disconnect/reconnect cycles in quick
  succession. Catches races in the producer/consumer cleanup paths.
- **Disconnect during peripheral lease.** Unplug while an `I2cLease`
  is active; assert the lease's next operation faults cleanly and
  disposing the lease doesn't deadlock.
- **Configuration / interface re-claim.** Unplug, replug, assert the
  re-opened device successfully re-claims the interface (some kernels
  hold the previous claim briefly).

Each of these maps to a real production failure mode the original SDK
either crashed on, hung on, or silently mis-handled.

---

## Setup recipes

### Linux native (the simple case)

You need a udev rule so the test user can write to
`/sys/bus/usb/devices/.../authorized` without sudo. One-time setup:

```bash
# /etc/udev/rules.d/70-periphery-test.rules
SUBSYSTEM=="usb", ATTR{idVendor}=="10c4", ATTR{idProduct}=="8a7e", \
    GROUP="plugdev", MODE="0664", \
    RUN+="/bin/sh -c 'chmod g+w /sys$devpath/authorized'"
```

```bash
sudo udevadm control --reload-rules
sudo usermod -a -G plugdev $USER     # log out + back in
```

After this, `LinuxAuthorizedHarness` can toggle the device without
prompting for sudo.

### WSL2 (Windows host, Linux test runner)

```powershell
# On Windows, one-time as Administrator:
winget install --interactive --exact dorssel.usbipd-win
usbipd list                    # find your busid
usbipd bind --busid 2-3        # share with WSL
```

```bash
# Inside WSL2:
sudo apt install linux-tools-generic hwdata
sudo update-alternatives --install /usr/local/bin/usbip usbip \
    /usr/lib/linux-tools/*/usbip 20
```

After that, `WslUsbipdHarness` shells out to `usbip attach/detach`
with the busid.

### QEMU/KVM with passthrough (cross-OS testing)

Add a QMP socket to your VM launch command:

```bash
qemu-system-x86_64 \
    -enable-kvm -m 4G -smp 4 \
    -drive file=guest.qcow2 \
    -usb -device qemu-xhci,id=xhci \
    -qmp unix:/tmp/qmp-test,server,nowait \
    ...
```

Pre-attach the device once at boot via `-device usb-host,...` if you
want it present at startup, or rely on `device_add` to attach it
later. `QemuQmpHarness` connects to the socket via `System.IO.Pipes`
and issues JSON-RPC commands.

For a Windows guest to test WinUSB, give the guest a recent Windows
USB stack image and ensure the device gets bound to WinUSB on first
attach (Treehopper's ships a `.inf` that does this).

### Smart hub (physical-level testing)

Compatible hubs from [uhubctl's list][uhubctl-supported]. Wire
Treehopper into a known port. Add a sudoers entry so the test user
can run `uhubctl` without prompting:

```
%wheel ALL=(root) NOPASSWD: /usr/bin/uhubctl
```

`UhubctlHarness` shells out to `sudo -n uhubctl`.

[uhubctl-supported]: https://github.com/mvp/uhubctl#compatibility

---

## What this doesn't cover

Honest about the gap: the harness drives **real** boards through
**real** lifecycle events. Things you still can't easily test this
way:

- **Error injection.** Real boards behave correctly. To exercise
  Periphery's error-handling paths (transfer stall, descriptor
  parse failure, malformed firmware response, claim refused), you
  need a programmable USB device that can be told to misbehave.
- **Firmware version skew.** Real boards are at current firmware.
  Testing the "firmware too old" path needs a board with old
  firmware (rare and getting rarer) or a synthetic device.
- **Wire protocol regression testing.** When upstream firmware ships
  a new version, real boards update; the harness can't run the test
  suite against the prior version.
- **Headless CI without physical hardware.** The harness still
  requires a real Treehopper present. Self-hosted runner with a
  permanently-attached board is the realistic answer.

These cases are exactly where a USB **gadget** (Linux gadgetfs +
`dummy_hcd` running a userspace process that emulates Treehopper's
firmware) earns its keep. Deferred for now; revisit when one of the
above gaps starts costing real time.

For the higher tier — verifying that the *physical signals on the
wires* match what the device was told to do — see
[`wire-level-testing.md`](wire-level-testing.md). That tier
catches a different class of bugs (protocol-encoding, mode-bit,
clock-divider) that no amount of OS-level lifecycle testing can
reach.

---

## Implementation order

When you actually start writing the integration tests:

1. **`LinuxAuthorizedHarness`** first. Smallest and most useful.
   Covers the daily dev loop and CI on a self-hosted Linux runner
   with a real Treehopper plugged in.
2. **`WslUsbipdHarness`** second if anyone develops on Windows.
3. **`QemuQmpHarness`** when there's a need to test the WinUSB
   backend without a Windows runner — i.e. when the cross-OS
   coverage gap shows up as a real bug.
4. **`UhubctlHarness`** last, as the pre-release smoke-test path.

The interface is small enough that adding a flavor takes an afternoon
once a test rig is wired up.

---

## References

- [Linux kernel USB authorization documentation][usb-auth]
- [usbipd-win][usbipd-win]
- [QEMU USB passthrough docs][qemu-usb]
- [uhubctl][uhubctl]
- [ADR-0038 — Periphery.Usb](../adr/0038-periphery-usb.md)
- [ADR-0039 — Periphery.Treehopper](../adr/0039-periphery-treehopper.md)
- [Plan: Periphery.Usb + Periphery.Treehopper](../plans/periphery-treehopper.md)

[usb-auth]: https://docs.kernel.org/usb/authorization.html
[qemu-usb]:  https://www.qemu.org/docs/master/system/devices/usb.html

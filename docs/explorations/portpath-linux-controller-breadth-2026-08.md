# PortPath across Linux controller types — the rig half of `#303`

**Date:** 2026-08-24 · **Rig:** the Linux device rig (a Proxmox VM) ·
**Kernel:** 6.8.0-138-generic · **Issue:** [#303](https://github.com/charles8051/periphery/issues/303)

Connection detail — host, address, credentials — is deliberately not recorded here, as
[`device-emulation-and-graph-walking-2026-08.md`](device-emulation-and-graph-walking-2026-08.md)
already establishes for this rig.

ADR-0079 D2's Linux grammar and D4's hub-count formula were measured once, against a
QEMU nested-hub fixture that hung everything off a **single xHCI controller**. Nothing
established that the nesting shape holds for a controller of a different kind. This
closes that half of `#303` — the half that needs no new hardware, only different QEMU
device models on the rig already in place.

The remaining half of `#303` still wants an ARM SBC: a machine where USB hangs off a
**platform** bus rather than PCI. See "what this does not settle".

## What was already there, and what was missing

The rig's `lsusb -t` showed UHCI, EHCI and xHCI controllers all along — but **every
non-xHCI bus was root-hub-only**, so their syspath chains were never exercised. The gap
was not the controllers; it was that no device had ever been attached to them.

## What changed on the rig

The rig VM's `args` gained an OHCI controller (a fourth type, absent entirely) and a UHCI
controller, each carrying a real chain:

```
-device pci-ohci,id=pohci
  -device usb-hub,id=oh1,bus=pohci.0,port=2
  -device usb-hub,id=oh2,bus=pohci.0,port=2.1
  -device usb-tablet,id=oleaf1,bus=pohci.0,port=2.1.1     → 2 external hubs

-device ich9-usb-uhci1,id=puhci
  -device usb-hub,id=uh1,bus=puhci.0,port=2
  -device usb-mouse,id=uleaf1,bus=puhci.0,port=2.1        → 1 external hub
```

**EHCI cannot carry this fixture, and the reason is real rather than a QEMU quirk.** A
first attempt wired the same hub chain to `usb-ehci` and the VM refused to start:

```
kvm: -device usb-hub,id=eh1,bus=pehci.0,port=2: Warning: speed mismatch trying to
attach usb device "QEMU USB Hub" (full speed) to bus "pehci.0", port "2" (high speed)
```

EHCI is high-speed only; full-speed devices reach it through a companion UHCI/OHCI
controller, which is exactly why a real ICH9 exposes both. So EHCI contributes only its
root-hub row here. That is recorded rather than quietly dropped.

## Ground truth

The kernel's own **`devpath`** attribute, read from each device, spells the port chain
directly (`2.1.1`). It is the Linux analogue of the Windows probe's independent devnode
walk: it comes from the device, not from parsing the string under test. A root hub
spells it `"0"` — one component, so `components − 1` lands on zero, the same answer the
parser reaches from an empty hop vector.

Captured with:

```bash
for d in /sys/bus/usb/devices/*; do
  echo "$(basename $d),$(readlink -f $d),$(cat $d/busnum 2>/dev/null),$(cat $d/devpath 2>/dev/null)"
done
```

## Result

**Four controller types, 42 assertions, no disagreements.**

| Bus | Controller | Chain | External hubs |
| --- | --- | --- | --- |
| 1 | `ohci-pci` | hub → hub → device | 0, 1, 2 |
| 4 | `uhci_hcd` | hub → device | 0, 1 |
| 2, 3 | `ehci-pci` | root hub only (see above) | 0 |
| 11 | `xhci_hcd` | hub → hub → device | 0, 1, 2 |
| 13 | `xhci_hcd` behind a **two-level PCI bridge** | passed-through camera | 0 |

Bus 13 is the incidental find: its controller sits at
`/sys/devices/pci0000:00/0000:00:1e.0/0000:05:02.0/0000:07:1b.0/usb13`, three PCI
components deep instead of one. That is the closest this rig gets to the varying-prefix
shape `#303` wants an SBC for, and the parser handles it because the Linux grammar
constrains nothing before `usbN` — it searches for that component rather than requiring
a fixed prefix. **The Windows grammar is the opposite** and requires `PCIROOT` first,
which is why [#304](https://github.com/charles8051/periphery/issues/304) is the sharper
of the two remaining measurement gaps.

The rows are now a permanent CI fixture in
`tests/Periphery.Tests/Model/PortPathLinuxRigTests.cs` — real captured paths, asserted
against their own `devpath`. They need no rig to run, because ADR-0079 D2 dispatches on
the shape of the string rather than the host OS.

**Negative control:** collapsing the Linux controller prefix so two controllers look
alike fails `DifferentControllers_ShareNothing_EvenOnOneMachine`, 1 of 42. A suite never
observed failing is not evidence.

## Rig state afterwards

The added OHCI/UHCI chains were **left in place** — they cost nothing and make the rig
better fixture hardware than it was. The rig VM's original `args` were:

```
-device qemu-xhci,id=pxhci -device usb-kbd,bus=pxhci.0 -device usb-mouse,bus=pxhci.0
-device usb-hub,id=h1,bus=pxhci.0,port=3 -device usb-hub,id=h2,bus=pxhci.0,port=3.1
-device usb-tablet,id=leaf1,bus=pxhci.0,port=3.1.1
```

**Restarting the VM broke its device-test capability, and this is worth knowing before
anyone else restarts it.** Unattended-upgrades had moved the kernel 6.8.0-124 → -138
while the VM was up, so the old kernel was still running; the restart booted the new one,
where `linux-modules-extra` was absent. `uhid` and `v4l2loopback` could not load,
`/dev/video10` was gone, and `periphery-uhid-ups` sat in `activating`. This is the drift
already documented for this rig, with a new trigger: *a restart is enough to trip it,
without any kernel work of your own.*

Repaired with `apt-get install linux-modules-extra-$(uname -r)`, `depmod -a`, `modprobe
v4l2loopback uhid`, and restarting the service. Verified by observable state — modules in
`lsmod`, `/dev/video10` present, service `active`.

One trap in that repair, because it cost a cycle: **`dpkg -l <pkg>` exits 0 even for a
package in `un` state** (never installed), so `dpkg -l … && echo present` reports a
package that is absent. Verify by file — `find /lib/modules/$(uname -r) -name 'uhid.ko*'`
— not by exit code.

## What this does not settle

- **Non-PCI-rooted USB.** Every controller here is PCI. An ARM SBC (a Raspberry Pi will
  do) would put USB under `/sys/devices/platform/…`, which is the prefix shape `#303`
  names. The parser should handle it — it constrains nothing before `usbN` — but should
  is not measured.
- **EHCI with a chain**, which needs a companion-controller topology rather than a
  standalone EHCI.
- **Anything about Windows.** `#304` is separate and, given the `PCIROOT`-first
  requirement, more likely to find a real defect.

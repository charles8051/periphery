# Device emulation on Linux, and what it buys device-tree graph walking — exploration

**Date:** 2026-08-23
**Status:** Exploratory. No decision taken; nothing here supersedes an ADR. Written to
answer two questions asked together, because the answer to the second one turned out to
constrain the answer to the first.
**Method:** Measured the actual Linux device rig (kernel 6.8.0-124) and the
actual source tree rather than reasoning from what the tools are supposed to support. The
measurements in the table below each carry a reproducing command in the appendix, and the
`ParentId` result is a **runtime** measurement rather than a source search. Narrative
references to past incidents and to open issues are cited, not re-measured.
**Scope:** Linux-side device emulation as a testing substrate; the planned first-class
device-tree graph walking work; the relationship between the two. Does **not** cover
Windows or macOS emulation, and deliberately argues against treating Linux emulation as a
substitute for either.

---

> **Update (2026-08-23).** [ADR-0078](../adr/0078-device-topology-is-a-rooted-forest.md)
> (*device topology is a rooted forest, and the forest is a snapshot*) and
> [ADR-0079](../adr/0079-port-path-is-a-parsed-value.md) (*port path is a parsed value*)
> were proposed in parallel with this document and were not consulted while it was written.
> They **decide** several things listed below as open questions — see *Open questions*, where
> each is now marked. They do not contradict anything here; ADR-0078 independently reaches the
> same functional-core conclusion Part 3 argues for. Sections below are otherwise preserved as
> the original point-in-time survey.

> **Update (2026-08-24).** **ADR-0078 was Rejected.** Its own reject trigger fired —
> ADR-0079 shipped and no consumer asked for the graph-shaped questions a parser cannot
> answer. Wherever this document marks an open question as *"decided by ADR-0078"* or
> *"ADR-0078's blocking item"*, read that as **decided by a decision that was then
> rejected**: the graph is not being built, so those questions are moot rather than
> answered. The one part of ADR-0078 that survives is D10, split out as
> [ADR-0080](../adr/0080-ancestor-walking-is-one-fold.md) (*ancestor walking is one shell
> fold*). The **measurements** in this document are unaffected — the QEMU nested-hub
> fixture, the `SUBSYSTEM` cut, and the 785-devices-0-parents probe are facts about the
> platform, and the first two are cited by ADR-0079, which is Accepted.

---

## The two questions

1. How far should we go using Linux to emulate the devices Periphery drives — not only for
   platform-specific tests, but for core behaviour where it is applicable?
2. Would that help the planned first-class graph walking of the device tree?

Short version: **further than we currently do, but along one specific axis, and the graph
work needs much less of it than it first appears** — because most of the graph logic should
not touch a device at all.

---

## What the rig actually is today

This section is the spine of the document. Several conclusions below follow from
measurements that contradict the obvious assumption.

| Question | Measured answer |
|---|---|
| How deep is the USB tree on the rig? | **Depth 1 for every attached device**, as measured 2026-08-23. 12 root hubs, no nested hub present. Snapshot — see caveat and captured output below. |
| Does Linux populate `ParentId` at runtime? | **No.** 785 devices enumerated, **0** with a non-null `ParentId`. |
| Which platforms *implement* it? | Windows only. `WindowsDeviceProvider.cs:200` assigns it; `LinuxDeviceProvider.cs` and `MacOSDeviceProvider.cs` exist and derive no parentage at all. |
| Are the UVC/HID/storage gadget functions present? | Yes — `usb_f_uvc`, `usb_f_hid`, `usb_f_mass_storage`, `usb_f_midi`, `usb_f_printer`. |
| Can a gadget actually be instantiated? | **No.** `/sys/class/udc/` does not exist. |
| Is `dummy_hcd` available to provide a virtual UDC? | **No.** `# CONFIG_USB_DUMMY_HCD is not set` in Ubuntu's kernel; not built as a module. |
| What UDC drivers *are* shipped? | `amd5536udc_pci`, `goku_udc`, `gr_udc`, `max3420_udc`, `mv_udc`, `mv_u3d_core`, `bdc`, `cdns2` — all for physical controllers. This row is explanatory only; the *proof* that no UDC is usable is the empty `/sys/class/udc/` above, which is the binding the kernel would have created had any driver matched. |
| Can QEMU nest USB hubs? | `usb-hub` is an available device type on the host. Nesting needs explicit bus/port wiring — see the caveat in Part 2. |
| Is `vhci-hcd` (usbip) available? | Yes, shipped as a module. |

**Snapshot caveat.** `lsusb -t` reports only what is attached when it runs. "Depth 1" is a
statement about the rig as configured on 2026-08-23, not a permanent property — attaching a
physical hub, or a future QEMU change, would invalidate it. The captured output:

```
/:  Bus 001.Port 001: Dev 001, Class=root_hub, Driver=uhci_hcd/2p, 12M
/:  Bus 002.Port 001: Dev 001, Class=root_hub, Driver=ehci-pci/6p, 480M
    … buses 003–008 likewise, root hub only …
/:  Bus 009.Port 001: Dev 001, Class=root_hub, Driver=xhci_hcd/4p, 480M
    |__ Port 001: Dev 002, If 0, Class=Human Interface Device, Driver=usbhid, 480M
    |__ Port 002: Dev 003, If 0, Class=Human Interface Device, Driver=usbhid, 480M
/:  Bus 010.Port 001: Dev 001, Class=root_hub, Driver=xhci_hcd/4p, 5000M
/:  Bus 011.Port 001: Dev 001, Class=root_hub, Driver=xhci_hcd/15p, 480M
    |__ Port 001: Dev 002, If 0, Class=Video, Driver=uvcvideo, 480M
    |__ Port 001: Dev 002, If 1, Class=Video, Driver=uvcvideo, 480M
    |__ Port 001: Dev 002, If 2, Class=Audio, Driver=snd-usb-audio, 480M
    |__ Port 001: Dev 002, If 3, Class=Audio, Driver=snd-usb-audio, 480M
/:  Bus 012.Port 001: Dev 001, Class=root_hub, Driver=xhci_hcd/15p, 5000M
```

Every non-root entry is a direct child of a root hub. The four `Class=Video` / `Class=Audio`
lines are *interfaces* of one composite device, not four devices — worth noting because a
naive walker will present them as siblings.

Two rows deserve emphasis because they invert the intuitive plan.

**The gadget path is not currently available.** The presence of `usb_f_uvc` is misleading:
a configfs gadget needs a USB Device Controller to bind to, a VM has none, and the standard
virtual substitute (`dummy_hcd`) is compiled out of Ubuntu's kernel. So "just declare a
camera with the descriptor we want" — the obvious answer to
[#275](https://github.com/charles8051/periphery/issues/275)-style capability gaps — is a
kernel-config or DKMS project, not an afternoon.

**Graph walking currently has neither a data source nor a fixture on Linux.** ADR-0002
(*Device Tree Topology & USB Enrichment*, Accepted 2025-07-15) specifies `ParentId` across
all three platforms and names `CM_Get_Parent` / sysfs / IOKit as the three sources. Only the
Windows one was built; on Linux the field is null for all 785 devices. And even if the sysfs
source were written tomorrow, the rig's tree is flat, so nothing would exercise a traversal
past the first hop.

*The fixture half of that has since been fixed* — a nested hub chain was built the same day
(Part 2), which is what let ADR-0078's blocking question be answered. The data-source half
stands: Linux still derives no parentage.

---

## Part 1 — Where emulation earns its place

The proposed line:

> **Emulate a device's declared shape and protocol. Do not try to emulate its physical
> failure modes.**

### Where it is worth real investment

**Capability variation — the strongest case, and not about convenience.** A purchased device
gives one point in a space. Periphery's job is to behave correctly across the *space*.
[#275](https://github.com/charles8051/periphery/issues/275) is the worked example: the bug
lived entirely in a capability combination (a UVC camera whose `bmAutoExposureMode` omits
`AUTO`), the rig's camera advertises all four modes, and so the rig could not reproduce it
at any price. The fix for "we need a worse camera" is to *declare* one. That is coverage
money cannot buy, and it is the only argument here strong enough to justify the kernel work
the gadget path needs.

**Adversarial shapes.** A device with zero controls; one control; a control reporting
`DISABLED`; a menu with a single entry; a descriptor that is legal but eccentric. Nobody
sells these, and they are where boundary bugs live.

**Contract and lifetime behaviour.** [#256](https://github.com/charles8051/periphery/issues/256)
(ioctl on a recycled fd), `#259`/`#261` (teardown ordering). These are logic defects; an
emulated device reproduces them exactly, because the physical layer was never involved.

### Where it does not substitute, and should not be allowed to

**Physical-layer faults.** [#260](https://github.com/charles8051/periphery/issues/260) (hub
flapping), [#224](https://github.com/charles8051/periphery/issues/224) (no VBUS drop on port
cycle), marginal cabling — the rig's own webcam once failed to enumerate on a bad front
port. No gadget reproduces any of this. A green emulated suite says nothing about it, and
the risk is that it *feels* like it does.

**The other two platforms.** This is the failure mode to guard hardest against. Periphery
targets Windows, Linux and macOS; the Windows Media Foundation and WinUSB backends are among
the highest-churn code in the repository; **no amount of Linux emulation touches a line of
them.** Linux emulation is cheap, pleasant, and fully under our control, which makes it an
extremely comfortable place to spend effort while Windows and macOS coverage quietly stalls.
That is a real organisational hazard, not a hypothetical one — see the non-substitution rule
in Part 3, which exists precisely because this document's own sequencing would otherwise
model the hazard it warns about.

**Timing and performance realism.** Gadget-driver latency is not device latency. Anything
cadence-sensitive should be tested against the pure core with an injected clock, not against
an emulated device with a different-but-also-wrong timing profile.

### The cost nobody quotes

**Every fixture is rig state that rots silently.** In one working session this month:
`linux-modules-extra` drift removed `/dev/video10` after a kernel bump; vivid needed
`modprobe.d` pinning to hold `/dev/video20`; and the Actions runner sat wedged for two
months while its job reported `SKIPPED`, which reads as "not applicable" rather than "your
rig is unreachable". Each new fixture is another thing that can fail in a way that looks
like nothing happening.

So the operating rule should be:

> Add a fixture when it buys coverage that is otherwise **unobtainable**. Not when it merely
> saves plugging something in.

By that rule the vivid node was worth it (it is the only source of V4L2 control behaviour
without a camera), and a gadget-based UVC camera would be worth it (it is the only route to
descriptor variation) — but a gadget replacement for a webcam we already own would not be.

---

## Part 2 — Graph walking specifically

Emulation helps here more than anywhere else so far. The reason is narrow and worth stating
precisely: **the failures that break tree code are topological, and topology is exactly what
we cannot obtain physically without buying and cabling hardware.**

### What was untestable — and what still is

As written, the rig's tree was flat and every one of these was unreachable:

- traversal past depth 1 — ancestry, descendants, depth limits
- a device removed **mid-walk**, invalidating a parent reference held during traversal
- orphans, where a filter excludes a parent but retains its children
- pathological fan-out, and stable sibling ordering
- cycles or self-parenting from a malformed or duplicated identifier
- composite devices, whose interfaces must not be mistaken for sibling devices

**The nested-hub fixture (below) reached the first and the last of those.** Depth-3 ancestry
is now observable, and interface nodes appear as children of their device rather than as
siblings. The rest are unchanged, and most of them should never need a fixture: orphan
placement, cycles and fan-out are properties of a graph *value*, so they belong to the pure
core of Part 3 and are cheaper to provoke with a constructed input than with hardware.
Mid-walk removal is the one genuinely shell-side case still unexercised.

### An issue this document got wrong about graph walking

[#231](https://github.com/charles8051/periphery/issues/231) — device instance IDs changing
case across re-enumeration (`CDYHINBH` → `cDYhINBh`).

**Corrected 2026-08-23.** This section originally claimed the issue becomes structural under
graph walking, because a case flip would fragment a parent-keyed tree into orphans. That
overstated it: `DeviceId` is **already** case-insensitive — `Equals` and `GetHashCode` both
use `OrdinalIgnoreCase` — so a `DeviceId`-keyed join is correct by construction and `#231`
cannot fragment it.

The real hazard is narrower and is an implementation trap rather than an inherent one: a
graph keyed on raw `string`, or on a map built with the default comparer, *would* fail to
resolve a case-varied parent — and the failure would not look like one. It would surface as
a legitimately named state (filtered, or phantom), because those states exist precisely to
describe a parent id that resolves to nothing. That constraint is now recorded in ADR-0078
D3 rather than left as a warning here.

### What emulation is cheap for here — with an honest caveat

QEMU supports `usb-hub`, so nested topology needs no kernel work, no gadget and no UDC,
which makes it far and away the cheapest route to a tree with real depth.

**It is not, however, a one-line change, and this document originally overstated that.**
`-device usb-hub` declares a *single* hub; building `hub → hub → device` requires explicit
bus and port wiring, roughly of the shape:

```
-device qemu-xhci,id=xhci                        # the controller the tree hangs from
-device usb-hub,id=h1,bus=xhci.0,port=2          # hub on root port 2
-device usb-hub,id=h2,bus=xhci.0,port=2.1        # hub behind port 1 of h1
-device usb-storage,bus=xhci.0,port=2.1.1,drive=…   # device behind port 1 of h2
```

Ports are dotted paths down the tree, and every child names the same `bus` as the
controller rather than its immediate parent — which is the part most likely to be got
wrong on a first attempt.

**Exercised on the rig 2026-08-23 — the wiring above is correct.** Applied to the rig VM as:

```
-device usb-hub,id=h1,bus=pxhci.0,port=3
-device usb-hub,id=h2,bus=pxhci.0,port=3.1
-device usb-tablet,id=leaf1,bus=pxhci.0,port=3.1.1
```

appended to the existing `args` (which already declared `qemu-xhci,id=pxhci` with a
keyboard and mouse). The guest enumerates the full chain:

```
Bus 009 root_hub (xhci_hcd/4p)
    |__ Port 003: Dev 004, Class=Hub          ← h1   (0409:55aa)
        |__ Port 001: Dev 005, Class=Hub      ← h2
            |__ Port 001: Dev 006, Class=HID  ← leaf1 (0627:0001)
```

Two points confirmed that the caveat above was right to flag. Every child names the
**controller** as its `bus`, not its immediate parent, and the port is the dotted path —
`port=3.1.1`, not `port=1` on `h2`. Get either wrong and QEMU either refuses to start or
silently attaches the device to the root hub, which looks like success.

The resulting depth-3 tree is what let ADR-0078's blocking `LeftSnapshot` question be
answered; the measurements are recorded there and in ADR-0079 D2/D4. The VM's original
config is backed up on `core` at `/root/vm172-config-backup-20260822-211324.txt`.

---

## Part 3 — The recommendation

**Most of the graph work should need no device at all.**

Following the repository's stated functional-core/imperative-shell preference, the walk
itself is a pure function:

```
set of (id, parentId) pairs  →  graph
```

Cycle detection, orphan policy, depth limiting, ordering, ancestor/descendant queries — all
of it is a value transform over immutable inputs. No IO, no clock, no device. A ten-thousand
node adversarial tree can be tested in microseconds, deterministically, on every platform,
in the ordinary unit suite. That is where the genuinely tricky logic lives, and it is also
where the tricky *bugs* will live.

Emulation then covers only the thin acquisition edge — *does the Linux backend read sysfs
parentage correctly for a real nested tree?* — which is a handful of tests against a QEMU hub
tree, not a testing strategy.

### The non-substitution rule

The sequencing below is deliberately Linux-shaped, which is exactly the hazard named in
Part 1. So it comes with a standing constraint:

> **Linux emulation results never satisfy a Windows or macOS coverage requirement.** A green
> emulated suite is evidence about the pure core and the Linux backend, and about nothing
> else. Any graph-walking work that ships must carry Windows validation — and macOS
> validation once a provider exists — as a *separate, named* acceptance criterion, not as
> something inferred from steps 1–4 passing.

Concretely, step 3 below should not be considered complete until the equivalent Windows
behaviour is exercised too. `ParentId` already exists there and is untested by anything in
this plan.

### Suggested sequencing

1. **Pure core first.** The graph as a value transform, exhaustively unit-tested with no
   hardware. Settle orphan and cycle policy here, where it is cheap to change. Runs on all
   three platforms by construction.
2. **Key the join on `DeviceId`, never on `string`.** Not a prerequisite task — a constraint
   on how step 1 is written, satisfied for free by using the existing type, whose equality is
   already `OrdinalIgnoreCase`. [#231](https://github.com/charles8051/periphery/issues/231) is
   *not* a blocker for graph work; it remains open on its own merits (identity and display),
   and this document previously and wrongly claimed otherwise.
3. **Populate `ParentId` on Linux** (sysfs hierarchy), closing the ADR-0002 gap — *paired
   with* Windows coverage of the existing implementation, per the non-substitution rule.
4. **One QEMU nested-hub fixture** to prove the acquisition edge, after spiking the wiring
   above. Cheap; do it when step 3 needs verifying.
5. **Gadgets only if a specific capability gap demands them**, accepting that this needs
   `dummy_hcd` — a kernel-config or DKMS project — and should be justified by a concrete gap
   such as `#275`'s, not by general appeal.

### Definition of done, per step

Stated explicitly because a numbered list of Linux-shaped tasks otherwise implies that
finishing them finishes the work. **No step below closes on Linux evidence alone.**

| Step | Closed by | Explicitly does *not* close on |
|---|---|---|
| 1. Pure core | Unit suite green on Windows, Linux and macOS — it has no platform dependency, so all three are free | — |
| 2. `DeviceId`-keyed join | A test that a case-varied parent id still resolves — cheap, pure, no device | a raw-`string` map passing on ids that happen not to vary |
| 3. `ParentId` on Linux | sysfs implementation **and** the pre-existing Windows implementation exercised — it is currently tested by nothing | the Linux half alone |
| 4. QEMU fixture | The acquisition edge verified against a nested tree | anything, on its own — it is evidence about Linux sysfs reading and nothing else |
| 5. Gadgets | A named capability gap it closes | general appeal |

macOS has no provider parentage today, so it has no step here. That is a **gap to be tracked,
not an exemption**: if graph walking ships as a public surface, macOS returning null parents
everywhere is a platform-parity defect, not a missing nice-to-have.

A useful smell test: **if the graph work begins by building an emulation rig, the core was
not factored correctly.**

---

## Open questions

- **Is a graph a first-class returned type, or a view over enumeration?** **Moot.**
  ADR-0078 D3 answered it — an immutable graph value built once from a snapshot, with pure
  total queries over it — and **ADR-0078 was Rejected on 2026-08-24**. There is no graph, so
  the question has no live subject. It returns if a consumer ever asks for one.
- **What is the orphan policy?** When a filter excludes a parent, do its children surface at
  the root, vanish, or attach to a synthetic placeholder? **Moot for now, and never answered.**
  ADR-0078 D4/D8 named the *states* a missing parent can be in, and that ADR was Rejected;
  the placement policy was never settled by anything. Nothing enumerates a parent relation
  to have orphans in, so there is no policy to have.
- ~~**How are composite devices modelled?**~~ **Partly addressed, by ADR-0079 only.**
  Its parsing of interface-bearing paths ships: `USBMI(n)` on Windows and `9-3.1.1:1.0` on
  Linux are discarded at parse time rather than counted as hub hops. ADR-0078's container
  relation (D2) went with the rejection, so grouping a composite device's interfaces is still
  a `ContainerId` group-by a consumer writes itself. The captured tree above — one webcam
  presenting four interfaces — remains a concrete fixture.
- ~~**Snapshot or live?**~~ **Answered: live, by elimination.** ADR-0078 argued for the
  snapshot and was Rejected, so the snapshot side does not exist. What survives is its D10 —
  the finding that the four shipping walks are live shell-side walks and are a different shape
  from any snapshot graph — now [ADR-0080](../adr/0080-ancestor-walking-is-one-fold.md).
- **Is `dummy_hcd` worth enabling at all?** It unlocks the whole gadget space, but means a
  DKMS module or a custom kernel on a rig that has already been broken twice by kernel drift.
  The honest answer may be to host gadgets on a small board with a real UDC in peripheral
  mode instead of on the VM.
- **What is the macOS story?** Both emulation and topology are unaddressed there. IOKit
  offers the traversal API per ADR-0002; `MacOSDeviceProvider.cs` derives no parentage.
  **Still open.** It was the whole of ADR-0078's blocking item, and that ADR is now Rejected,
  so it blocks nothing — it is a plain gap in macOS coverage. The item originally asked
  whether `LeftSnapshot` means the same thing on Linux *and* macOS. The Linux half was
  answered on 2026-08-23 by the nested-hub fixture described in Part 2 — the USB plane is
  closed under parentage up to the root hub, whose parent is a PCI node a USB-filtered
  enumeration does not contain. **That is a result about the substrate, not about Periphery.**
  It shows libudev's subsystem filtering produces a cut of the same shape as SetupAPI's
  class-GUID cut, which is what ADR-0078's item asked; it does not exercise `LeftSnapshot`
  through a Periphery snapshot, because the Linux provider still derives no `ParentId` to
  build one from. Exercising it end to end waits on step 3 of the sequencing below. So the
  blocker is narrower than when this was written, and
  its remaining cause is different: not that the rig lacks a tree with any interior, which it
  no longer does, but that there is no macOS provider parentage to cut and no macOS rig to
  cut it on.

---

## Appendix — reproducing the measurements

Access details for the rig — host, account, hypervisor node — are deliberately not recorded
here. Substitute `$RIG` for the rig and `$PVE` for the Proxmox host below.

**On the rig:**

```bash
# USB tree depth. Reports only what is attached when it runs -- a snapshot.
lsusb -t

# Is a gadget instantiable? (missing directory = no UDC = no)
ls -1 /sys/class/udc/

# Is dummy_hcd available to provide a virtual UDC?
grep -iE 'CONFIG_USB_DUMMY_HCD|CONFIG_USB_GADGET=' /boot/config-$(uname -r)

# Which gadget functions exist (present != usable, see above)
ls /lib/modules/$(uname -r)/kernel/drivers/usb/gadget/function/

# Which UDC drivers ship -- all target real silicon absent from a VM
ls /lib/modules/$(uname -r)/kernel/drivers/usb/gadget/udc/

# usbip virtual host controller availability
modinfo vhci-hcd
```

**On the Proxmox host:**

```bash
# Is the usb-hub device type available?
qemu-system-x86_64 -device help | grep usb-hub
```

**In the repo:**

```bash
# Which platform providers ASSIGN ParentId (not merely mention it)
grep -rn 'ParentId =' src/Periphery/Windows/ src/Periphery/Linux/ src/Periphery/MacOS/
```

**Runtime `ParentId` measurement.** The source search above shows which providers *implement*
the field; it cannot show what enumeration actually *returns*. The table's 785/0 figure came
from the probe below. It is reproduced in full — not summarised — so the central measurement
of this document can be independently re-run rather than taken on trust.

Save as `tests/Periphery.Tests/ParentIdProbe.cs`, run, then delete:

```csharp
using System.Linq;
using System.Threading.Tasks;
using Periphery;
using Xunit;
using Xunit.Abstractions;

namespace Periphery.Tests;

public class ParentIdProbe
{
    private readonly ITestOutputHelper _o;
    public ParentIdProbe(ITestOutputHelper o) => _o = o;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task MeasureParentIdPopulation()
    {
        var all = await Devices.Enumerate().ToListAsync();
        int withParent = all.Count(d => d.ParentId is not null);
        _o.WriteLine($"PARENTID-PROBE devices={all.Count} withParent={withParent}");
    }
}
```

```bash
# On the rig. The detailed logger is required -- ITestOutputHelper output is
# suppressed for passing tests at default verbosity, which is how a first run of
# this probe appeared to produce nothing at all.
export PERIPHERY_LINUX_DEVICE_TESTS=1
dotnet test tests/Periphery.Tests/Periphery.Tests.csproj --nologo \
  --filter "FullyQualifiedName~ParentIdProbe" \
  --logger "console;verbosity=detailed" | grep PARENTID-PROBE

# measured 2026-08-23: PARENTID-PROBE devices=785 withParent=0
```

It is deliberately **not** checked in. A permanent test asserting `withParent == 0` would
encode the absence of a feature as a requirement, and would have to be deleted the day a
Linux sysfs implementation lands — exactly when someone would most want to re-measure. Re-run
it rather than trusting the number.

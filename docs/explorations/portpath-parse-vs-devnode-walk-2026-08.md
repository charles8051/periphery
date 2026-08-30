# Port-path parsing vs the devnode walk — ADR-0079 D4 re-run

**Date:** 2026-08-22 · **Host:** the Windows workstation (Windows 11 Pro 26200) · **Probe:**
[`scratch/PortPathProbe`](../../scratch/PortPathProbe) · **Raw output:** five runs,
deliberately not committed — every row carried a real machine's device instance IDs,
which is hardware identity rather than evidence. The aggregates below are what the
argument rests on, and the probe regenerates the raw form on any machine.

Closes the blocking item ADR-0079 D4 named: the ADR's original 42-for-42
cross-validation covered `USB\VID_*` devices — the population whose
`LocationPath` the OS supplies directly — and excluded the `HID\` function nodes
where `WindowsDeviceProvider.ResolveLocationPath` *synthesizes* the path, which
is where both motivating consumers live.

## The contract, and how it is enforced

`scratch/PortPathProbe/SpecTests.cs` pins ADR-0079's decision as executable
assertions: both grammars, the rejection reasons, the hub-count formula, all five
relations, the three shallow cases where D5's implication chain breaks, and the
`default(PortPath)` behaviour D7 requires. It runs **before** any measurement and
fails the process on any violation.

This matters because the probe alone cannot catch a semantic regression: agreement
on hub counts says nothing about whether `SharesExternalHubWith` answers correctly.
Verified by injecting the exact defect D5 exists to prevent — removing the two-hop
floor — which produced 6 assertion failures and exit 1. A suite never observed
failing is not evidence of anything.

**What this does not do is constrain the shipping type.** `SpecTests` runs against
`SpecPortPath`, the transcription — so once `PortPath` exists in `src/`, this suite
would keep passing even if the production parser diverged from it. It is the
executable form of the *decision*, not enforcement of the *implementation*, and
treating it as the latter is exactly the false confidence this apparatus is
supposed to avoid.

So the carry-over is enforced rather than requested. **The probe fails the moment a
`PortPath.cs` appears anywhere under `src/`**, reporting that it has been superseded
and must be retired — porting these fixtures into `tests/` against the real type and
deleting `scratch/PortPathProbe`. Until that day the probe is a decision record with
a self-check; from that day it refuses to report success at all.

Note also that this project is **not in `Periphery.slnx`**, so CI never builds or runs
it. It cannot contribute a green check to anything; it is a local tool that has to be
run deliberately.

## How to reproduce

```bash
dotnet run --project scratch/PortPathProbe -- --out docs/explorations
```

Exit code 0 means the spec assertions hold **and** every parsed row was
independently corroborated — each non-root-hub row's walk reached a root hub, no
count disagreed, no instance-id fallback parsed, and no property read failed. 1
means the ADR's D4 is not discharged by that run.

The root-hub exception is the one narrow carve-out and it is checked rather than
assumed: such a row must terminate `NoParent`, because the walk looks for a root
hub among the *ancestors* and this node is one. An earlier version failed only on
outright disagreement, so a cfgmgr32 error or an odd parent chain produced
"parser-only" and still exited 0 — the probe reporting an unvalidated row as a
validated one, which is the very failure D7 forbids the API from committing. Windows-only, by design — ADR-0079
D2 scopes the grammar to Windows and the probe refuses to run elsewhere rather
than reporting a vacuous pass.

**What "independent" means here, and what it does not.** `CfgMgr.cs` is a second
cfgmgr32 implementation, not a call into `Periphery.Windows.DevNodeHelper` —
reusing the library's own helper would make agreement guaranteed. But both sides
still read the same devnode tree, so agreement shows the *parser* is faithful to
that tree, not that the tree is faithful to the machine. That distinction is
ADR-0079 D4's own framing and D8's limitation; nothing here closes it.

One deliberate difference from the original walk: hub-ness is decided by the
driver **service** (`USBHUB` / `USBHUB3`), not by the device description.
ADR-0078 describes the original as counting *"nodes named as hubs"*, and names
are localized and vendor-editable.

## Results

300 devices enumerated with `DeviceCategory.All`.

### A/B — parsed hub count vs independent parent walk

| Plane | n | Agree | **Disagree** | Parser only | Walk only | Neither |
| --- | --- | --- | --- | --- | --- | --- |
| `HID\*` | 21 | 21 | **0** | 0 | 0 | 0 |
| `USB\*` | 54 | 49 | **0** | 5 | 0 | 0 |

**The `HID\` column is the one D4 asked for: 21 of 21, no disagreements.** Every
one of those 21 nodes has an empty own `DEVPKEY_Device_LocationPaths` — the
Context claim, re-measured independently — so all 21 paths came from
`ResolveLocationPath`'s ancestor walk rather than from the OS, and the parser
still agrees with a walk that shares no code with it.

The five `USB\*` rows where the parser is conclusive and the walk is not are
**exactly the five root hubs** (`USB\ROOT_HUB30\…`). The walk terminates
`NoParent` because it looks for a root hub *among the ancestors* and this node
*is* one; the parser reports `IsRootHub = true` and 0 external hubs. That is not
a disagreement — it is the parser being strictly more informative than the walk
for that case.

### C — instance-id fallbacks must not parse (D7)

| | |
| --- | --- |
| `LocationPath == Id` (ResolveLocationPath fell back to the instance id) | **56** |
| …of which `TryParse` succeeded | **0** ✅ |

D7 requires that a fallback never reads as "zero hubs". Measured, not asserted:
all 56 fail with `MalformedSegment`, because an instance id has no `KIND(ARG)`
segments at all.

### D — the zero-hop split the ADR's Context table folded together

The original table reported a single row of `5` devices with zero `USB(n)`
segments, described as *"root hubs, and function nodes with no own path"*. Those
are two different states and the split is:

| | |
| --- | --- |
| Parsed, zero hops — **genuine root hubs** | **5** |
| Did not parse — **not port paths at all** | **204** |

The 204 break down as `NoUsbRoot` 96 (well-formed non-USB paths, e.g. a NIC's
`PCIROOT(0)#PCI(0200)`), `MalformedSegment` 56 (the instance-id fallbacks
above), `UnknownSegmentKind` 52 (see below). So the original `5` was root hubs
only; the non-paths were never in that tally, being counted by `USB(`
occurrences rather than by whether the string was a port path.

Hop distribution over the 96 parsed paths:

| Hops | Devices | External hubs |
| --- | --- | --- |
| 0 | 5 | 0 |
| 1 | 58 | 0 |
| 2 | 29 | 1 |
| 3 | 4 | 2 |

### D3's strict policy — measured cost: zero

ADR-0079 D3 decides that an unrecognised segment kind fails the whole parse,
accepting a missing answer over a possibly wrong one. This machine surfaces two
kinds the ADR had not seen — `ACPI(…)` (114 occurrences) and `UMB(…)` /
`UMBROOT(…)` (11) — across 52 rejected paths.

**None of those 52 contains a `USBROOT` segment.** They would have been rejected
as `NoUsbRoot` regardless. So on this machine strict parsing loses no answer it
could otherwise have given, which is the first evidence either way on the trade
D3 makes.

### E — `ResolveLocationPath`'s `maxDepth: 8` has headroom

| | |
| --- | --- |
| Nodes with empty own `LocationPaths` that resolved via an ancestor | 61 |
| Max ancestor depth to the first path-carrying node | **2** |
| Mean | 1.07 |
| Nodes where no ancestor carried a path (the fallback case) | 56 |

The bound is 8; the deepest real walk was 2. It is nowhere near binding on this
hardware, so the exhaustion case D4 listed cannot be produced by plugging things
in — it stays a synthetic test against the `lookupNode` seam, as the existing
`WindowsDeviceProviderTests` cyclic-chain case already is.

### F — fixtures for D5's relation tests

Real pairs from this machine, one per case:

```
same controller, DIFFERENT root ports — the D5 counterexample
  PCIROOT(0)#PCI(0301)#PCI(0000)#PCI(0800)#PCI(0003)#USBROOT(0)#USB(1)
  PCIROOT(0)#PCI(0301)#PCI(0000)#PCI(0800)#PCI(0003)#USBROOT(0)#USB(4)

same EXTERNAL hub (>= 2 hops, differ in the last) — and note the USBMI tail,
which is exactly the case a string-prefix comparison gets wrong
  PCIROOT(20)#PCI(0301)#PCI(0000)#USBROOT(0)#USB(6)#USB(4)#USB(2)#USBMI(2)
  PCIROOT(20)#PCI(0301)#PCI(0000)#USBROOT(0)#USB(6)#USB(4)#USB(2)#USBMI(0)

downstream-of (proper prefix), against the root hub itself
  PCIROOT(20)#PCI(0301)#PCI(0000)#USBROOT(0)#USB(8)#USBMI(0)
  PCIROOT(20)#PCI(0301)#PCI(0000)#USBROOT(0)

same port at ONE hop — IsSamePortAs = Yes, SharesExternalHubWith = No
  PCIROOT(0)#PCI(0301)#PCI(0000)#PCI(0800)#PCI(0003)#USBROOT(0)#USB(1)
  PCIROOT(0)#PCI(0301)#PCI(0000)#PCI(0800)#PCI(0003)#USBROOT(0)#USB(1)

different controllers, same PCI shape
  PCIROOT(0)#PCI(0301)#PCI(0000)#PCI(0800)#PCI(0003)#USBROOT(0)#USB(1)
  PCIROOT(0)#PCI(0301)#PCI(0000)#PCI(0800)#PCI(0001)#USBROOT(0)#USB(1)#USBMI(0)
```

The fourth pair is the shallow case ADR-0079 D5 records as breaking the
implication chain, and it occurs naturally here rather than needing to be
constructed.

## The motivating shape *is* in the data

The composite-HID-behind-cascaded-hubs case — the shape ADR-0078 D7 records for
kiosk B, and the one the UPS consumer cares about — is present and agrees:

```
HID\VID_0763&PID_003A&MI_02\A&1D462EE9&0&0000
PCIROOT(20)#PCI(0301)#PCI(0000)#USBROOT(0)#USB(6)#USB(4)#USB(2)#USBMI(2)

  hop_count               3      → parsed external hubs   2
  own LocationPaths       empty  → resolved at ancestor depth 1
  independent walk        ReachedRootHub, 2 external hubs
  agrees                  yes
```

Every property this ADR leans on is exercised at once by that single row: the
path is **synthesized** rather than read, the device is **composite** so the
trailing `USBMI(2)` is present, and it sits behind **two cascaded external
hubs** so the count is neither 0 nor 1. It is `n = 1`, which is why the
follow-ups below still matter — but the shape is measured, not assumed.

## Run 2 — the same machine with the hub cascade rearranged

A second snapshot was taken after moving a VIA two-chip hub
(`VID_2109` `PID_0817`/`PID_2817`) from behind one upstream hub to behind
another:

| | Run 1 | Run 2 |
| --- | --- | --- |
| `PID_0817` (USB3 side) | `USB(1)#USB(4)` | `USB(2)#USB(2)` |
| `PID_2817` (USB2 side) | `USB(5)#USB(4)` | `USB(6)#USB(2)` |

299 devices; `HID\*` **21 of 21**, `USB\*` 48 of 48, **0 disagreements**, 56
fallbacks of which 0 parsed. So the agreement survives a topology change rather
than being an artefact of one arrangement — which is the useful thing run 2
adds.

It did **not** add depth. The relocated hub is itself the deepest node on its
branch: nothing is plugged into it, so no path reaches `USB(2)#USB(2)#USB(n)`.
Cascading a hub into a hub only deepens the tree for the devices *below* the
downstream hub.

## Run 3 — devices populated below the cascade

A Logitech mouse (`VID_046D` `PID_C092`) and a Treehopper (`VID_10C4`
`PID_8A7E`) were then plugged into the downstream hub. 312 devices; `HID\*`
**28 of 28**, `USB\*` 53 of 53, **0 disagreements**, 56 fallbacks of which 0
parsed.

**This closes the `n = 1` concern.** The cascaded-hub HID population goes from 1
row to 8, across two vendors and two physically distinct hub chains:

| Chain | Device | Rows |
| --- | --- | --- |
| `USB(6)#USB(4)#USB(2)` | M-Audio `0763:003A`, composite | 1 |
| `USB(6)#USB(2)#USB(2)` | Logitech `046D:C092`, composite | 7 |

Note the arithmetic, because it is easy to get wrong by one: a device below a
hub that is itself below a hub sits at **three hops and two external hubs**, not
three external hubs. The downstream hub is the second external hub, and the
device below it is not a hub at all. Reaching three external hubs needs a third
cascaded hub, which this machine does not have.

One row in that set is worth calling out — a **virtual** HID node
(`HID\HID_DEVICE_SYSTEM_VHF\…`, Microsoft's Virtual HID Framework) parented
under the physical mouse, at `USB(6)#USB(2)#USB(2)` with no `USBMI` tail. It
resolves to its physical ancestor's port and agrees with the walk at 2. That is
the right answer — the VHF node genuinely is a child of that device — but it is
a reminder that a port path describes a **position**, and a virtual device
inherits the position of whatever it hangs off.

### The `Efm8HidProgrammer` fixture, live

Run 3 contains the exact scenario ADR-0079 D5 is written for: **two boards with
the same VID/PID at different depths under the same root port.**

```
USB\VID_10C4&PID_8A7E\CDYHINBH   …#USBROOT(0)#USB(6)#USB(3)          hops [6,3]    → 1 external hub
USB\VID_10C4&PID_8A7E\IMNUZ6YW   …#USBROOT(0)#USB(6)#USB(2)#USB(3)   hops [6,2,3]  → 2 external hubs
```

| Relation | Answer |
| --- | --- |
| `SharesControllerWith` | `Yes` |
| `SharesRootPortWith` | `Yes` — both on root port 6 |
| `SharesExternalHubWith` | `No` — different depths, so the two-hop-floor equality cannot hold |
| `IsDownstreamOf` | `No` — `[6,3]` is not a prefix of `[6,2,3]` |
| `IsSamePortAs` | `No` |

Two boards that a whole-string `ByLocationPath` comparison would only be able to
call "different", correctly separated into *same root port, different hubs* —
which is the distinction the EFM8 current-collision question actually needs.
Note also that the naive string prefix would have gone wrong here in the other
direction: `USB(6)#USB(3)` is not a string prefix of `USB(6)#USB(2)#USB(3)`, but
`USB(6)#USB(2)` **is** a prefix of `USB(6)#USB(2)#USB(3)`, so the hop-vector
comparison is doing real work on live data.

## Run 4 — a three-deep hub cascade, and transitive `IsDownstreamOf`

A third hub was cascaded into the second. 310 devices; `HID\*` **28 of 28**,
`USB\*` 52 of 52, **0 disagreements**.

It again added no *device* depth, for the reason run 2 already showed: the new
hub is the leaf of its branch. But it does add a fact none of the earlier runs
had — **a hub that is itself two hubs deep**, on both the USB2 and USB3 trees,
because the VIA part is a two-chip device:

| Tree | Level 1 | Level 2 | Level 3 |
| --- | --- | --- | --- |
| USB2 | Realtek `5411` `[6]` | VIA `2817` `[6,2]` | VIA `2817` `[6,2,4]` |
| USB3 | Realtek `0411` `[2]` | VIA `0817` `[2,2]` | VIA `0817` `[2,2,4]` |

That gives a **transitive `IsDownstreamOf` chain** on two independent trees:
`[6,2,4]` is downstream of `[6,2]`, which is downstream of `[6]`, and `[6,2,4]`
is downstream of `[6]` directly. Worth pinning as a test — transitivity is a
property the hop-vector prefix gives for free and a string prefix would also
appear to give, right up until a two-digit port number.

## Run 5 — three external hubs, and a third synthesis plane

A USB serial converter was plugged in below the third hub. 312 devices; `HID\*`
**28 of 28**, `USB\*` 53 of 53, **0 disagreements** — and the depth gap is
closed:

```
FTDIBUS\VID_0403+PID_6001+FTGDS53GA\0000
USB\VID_0403&PID_6001\FTGDS53G
  PCIROOT(20)#PCI(0301)#PCI(0000)#USBROOT(0)#USB(6)#USB(2)#USB(4)#USB(4)

  hop_count 4 → parsed external hubs 3 · independent walk ReachedRootHub, 3 · agrees
```

So `hops - 1` is now exercised at **0, 1, 2 and 3 external hubs**, agreeing with
the independent walk at every depth the machine can produce.

**`FTDIBUS\` is a third plane where the path is synthesized**, and that matters
because this document and the ADR had both described the empty-own-path
population as a `HID\` phenomenon. It is not. Across run 5, 70 nodes had an
empty own `DEVPKEY_Device_LocationPaths` and were filled in from an ancestor,
spanning **eleven** enumerators:

| Plane | Nodes | | Plane | Nodes |
| --- | --- | --- | --- | --- |
| `HID` | 28 | | `BTH` | 4 |
| `SWD` | 18 | | `SCSI` | 2 |
| `USB` | 8 | | `VHF`, `USBPRINT`, `HDAUDIO`, `FTDIBUS` | 1 each |
| `DISPLAY` | 4 | | *(one GUID-named enumerator)* | 2 |

Most of those resolve to non-USB paths and correctly fail to parse as
`NoUsbRoot`; the ones that matter here — `HID`, `USB`, `FTDIBUS` — resolve to
real port paths and all agree. The point is that `ResolveLocationPath` carries
far more of this ADR than the `HID\`-only framing suggested.

**Position is not identity, in live data.** Those two four-hop rows are
*different devices* — the USB node and its `FTDIBUS` child — at the *identical*
path. `IsSamePortAs` answers `Yes`, correctly: they occupy the same physical
port. No caller may read that as "the same device", which is what ADR-0079 D5
says and what this pair demonstrates.

## What these runs do not settle

- **One machine.** The totals differ from the ADR's original measurement (300
  devices / 96 parsed paths here, against 47 distinct paths / 42 devices there).
  The `PCIROOT(20)#PCI(0301)#PCI(0000)#USBROOT(0)` controller appears in both, so
  this is plausibly the same box with different peripherals attached — but that
  has not been confirmed, and the runs should not be added together.
- **~~No device sits at three external hubs.~~ Closed by run 5.** The count now
  agrees with the independent walk at 0, 1, 2 and 3 external hubs.
- **Still one machine, one OS build.** Every run above is the Windows workstation on Windows
  11 Pro 26200. Nothing here speaks to a different Windows version, a
  Thunderbolt dock, or a machine whose paths lead with a segment kind not seen
  in these five runs — and D8 already records that tunneled buses defeat the
  count regardless of how well it is parsed.
- **Nothing about Linux or macOS** — by design for *this* probe (ADR-0079 D2).
  Linux is measured separately against a declared QEMU topology; see
  [`device-emulation-and-graph-walking-2026-08.md`](device-emulation-and-graph-walking-2026-08.md)
  and ADR-0079 D2/D4. macOS is out by construction.
- **Nothing about physical topology.** See the note under "How to reproduce".

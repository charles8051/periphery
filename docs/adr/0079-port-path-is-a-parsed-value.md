---
title: "ADR-0079: A port path is a parsed value, not a string — and it already answers the hub questions"
status: "Accepted"
date: "2026-08-22"
authors: "@charles8051"
tags: ["architecture", "decision", "topology", "usb", "hub", "location-path", "windows", "linux", "macos", "functional-core"]
supersedes: ""
superseded_by: ""
---

# ADR-0079: A port path is a parsed value, not a string

## Status

Companion to ADR-0078, and deliberately much smaller than it. Carries ADR-0073
D1 (report observations, not verdicts), ADR-0073 D4's zero-value posture, and
the posture ADR-0068 set for unmeasured state.

Windows and Linux are both measured; macOS is out of scope by construction
(D2). **This ADR decides a design and does not implement it** — no `PortPath`
exists in `src/` yet. The contract it specifies is pinned by an executable
transcription and fixture suite (`scratch/PortPathProbe`), which is what the
implementation must satisfy — and which pins the *transcription*, not the
shipping type. Implementing `PortPath` therefore carries an obligation: port
those fixtures into `tests/` against the real type and delete the scratch copy,
or the suite becomes a decoy that passes while production diverges. That
obligation is a tripwire rather than a note — the probe fails as soon as a
`PortPath.cs` exists under `src/`. D4's blocking re-run **has been run — five times, across four deliberate
changes of hub topology, with 0 disagreements throughout** and the external-hub
count agreeing with an independent devnode walk at 0, 1, 2 and 3 hubs. It is
recorded in [`portpath-parse-vs-devnode-walk-2026-08.md`](../explorations/portpath-parse-vs-devnode-walk-2026-08.md). What remains is one machine on one Windows build,
which is a breadth limit rather than an open question about the decision.

## Context

ADR-0078 identifies three consumers that want to ask topological questions and
proposes a snapshot graph to answer them. Two of those three ask questions that
`DeviceInfo.LocationPath` **already contains the answer to**, on the platform
the motivating measurements came from, today:

| Consumer | Question | Already answerable from `LocationPath`? |
| --- | --- | --- |
| `Efm8HidProgrammer` | *"do these two boards share a hub or root port?"* | **yes** — a hop-vector comparison, though external hub, root port and controller are *three different* comparisons (D5) |
| the kiosk UPS check | *"how many external hubs to the root hub?"* | **yes** — a hop count |
| `ResolveLocationPath` | *"first ancestor carrying a port"* | no — it **produces** the path |

"Answerable" is not "answered". Neither site asks the question in code today:
`Efm8HidProgrammer` logs the raw strings for a human to diff, and the UPS check
does not exist yet. D6 closes the first of those, and is the reason this ADR
changes a call site at all rather than shipping a type nobody calls.

The library already treats this string as structural data. ADR-0063 ships
`DeviceCorrelationMode.ByLocationPath`, which correlates a device across a
bootloader mode switch by its physical port. But it does so by **whole-string
equality** — the weakest possible use of a structured value, and one that cannot
answer either question above.

So the string is load-bearing structure, handled as an opaque string, in a
codebase whose stated preference is *"rich value types over strings"*
(ADR-0002, in its `SerialPortName` / `HardwareId` line of reasoning).

**ADR-0002 does not straightforwardly agree, and the distinction matters.**
ADR-0002 D1 chose the *opposite* for the sibling relation — `ParentId` is
`string?`, *"opaque, platform-specific, no parsing needed. A `DeviceRef` struct
adds ceremony for no real benefit."* That reasoning holds for `ParentId` and
does not transfer to `LocationPath`, because the two strings are different
kinds of thing: **`ParentId` is an identity, and the only defined operation on
an identity is equality** — which `string` already gives you. `LocationPath` is
a **position**, and positions compose: they nest, they have prefixes, they can
be counted and compared at more than one granularity. Everything in D5 is an
operation that has no meaning on an identity. So this ADR does not reverse
ADR-0002 D1; it declines to extend it to a value that is structurally unlike
its subject.

### What the path actually looks like — measured

Windows, on a multi-controller box (47 distinct paths across present USB/HID
devices):

```
PCIROOT(20)#PCI(0301)#PCI(0000)#USBROOT(0)#USB(2)
PCIROOT(0)#PCI(0801)#PCI(0003)#USBROOT(0)#USB(1)#USB(2)#USBMI(0)
PCIROOT(0)#PCI(0801)#PCI(0003)#USBROOT(0)                          ← the root hub itself
```

`USBROOT(n)` marks the root hub explicitly. Each `USB(n)` after it is one
hub-port hop. Distribution measured on this machine:

| `USB(n)` segments | Devices | Meaning |
| --- | --- | --- |
| 0 | 5 | root hubs **and** nodes whose resolved path is an instance-id fallback — see below |
| 1 | 23 | directly attached to the root hub |
| 2 | 16 | one external hub |
| 3 | 3 | two cascaded external hubs |

**That first row folds together the two things D7 exists to keep apart**, and
*this* measurement did not separate them — the D4 re-run since has ([`portpath-parse-vs-devnode-walk-2026-08.md`](../explorations/portpath-parse-vs-devnode-walk-2026-08.md)):
on that machine the analogous figure is 5 genuine root hubs against 204 strings
that are not port paths at all, the latter having never been in the tally
because it counted `USB(` occurrences rather than asking whether the string was
a port path. A root hub is a parsed path with zero
hops; an instance-id fallback is *not a port path at all*. They are the same
row here only because the tally counted `USB(` occurrences without first asking
whether the string was a port path. Splitting that 5 is part of the re-run in
D4.

Note also that the 47 above counts **distinct paths across USB and HID nodes**,
while D4's cross-validation counts **42 `USB\VID_*` devices**. Those are
different populations — distinct paths versus devices, and USB-and-HID versus
USB-only — and this draft does not reconcile them. Neither number is a subset
of the other in any way that has been checked.

### The path is only usable because `ResolveLocationPath` already exists

A **function/interface** node has an **empty** `DEVPKEY_Device_LocationPaths`.
`WindowsDeviceProvider.ResolveLocationPath` walks to the nearest ancestor that
carries one and fills it in, which is exactly why `DeviceInfo.LocationPath` is
populated for such nodes.

**This is not a `HID\` phenomenon, which is how earlier drafts of this ADR
described it.** Measured across the D4 probe runs, **70 nodes on 11 different
enumerators** had an empty own property and were resolved from an ancestor —
`HID` (28), `SWD` (18), `USB` (8), `DISPLAY` (4), `BTH` (4), `SCSI` (2), and
`VHF`, `USBPRINT`, `HDAUDIO` and `FTDIBUS` besides. Most resolve to non-USB
paths and correctly fail to parse; the ones that reach a real port path — `HID`,
`USB`, `FTDIBUS` — all agree with the independent walk ([`portpath-parse-vs-devnode-walk-2026-08.md`](../explorations/portpath-parse-vs-devnode-walk-2026-08.md)).

This ADR therefore **depends on** that walk rather than replacing it, and
operates on Periphery's *resolved* `LocationPath`, never the raw property. That
dependency is also the sharpest gap in this ADR's evidence, and D4 says so.

## Decision

### D1. `PortPath` is a parsed value over `LocationPath`, with pure total queries

A `readonly struct PortPath` parsed from `DeviceInfo.LocationPath`: immutable,
no IO, no clock, same input → same output. It is the functional core with no
shell at all, because the shell already ran — the string is in hand.

**Parsing produces a representation, and every query reads the representation,
never the original string.** That is not an implementation note; it is the
decision, and D5 shows what goes wrong without it.

```
raw       "PCIROOT(0)#PCI(0801)#PCI(0003)#USBROOT(0)#USB(1)#USB(2)#USBMI(0)"
           └───────────── controller ──────────────┘└─── hops ───┘└dropped┘
parsed    Controller = "PCIROOT(0)#PCI(0801)#PCI(0003)#USBROOT(0)"
          Hops       = [1, 2]
```

- **Controller** — everything through `USBROOT(n)`, compared as a whole.
- **Hops** — the ordered `USB(n)` port numbers, as integers. `USBROOT` and
  `USBMI` segments are **discarded during parsing** (D3), so no query can
  rediscover the trap they represent.

```csharp
// ADR-0078 D8 already names this type for SameContainer. One type, not two —
// see the note below.
public enum Tri { Unknown = 0, No, Yes }

public readonly struct PortPath
{
    /// <summary>The only way to obtain a parsed value. False means "not a port path" (D7).</summary>
    public static bool TryParse(string? locationPath, out PortPath path);

    // Interrogative — each yields a value a caller could misread as a
    // measurement, so each is gated (D7).
    public bool TryGetExternalHubCount(out int count);   // D4
    public bool TryGetIsRootHub(out bool isRootHub);

    // Relational — total over the pair, three-valued, so no answer can be
    // misread as an established negative (D7).
    public Tri IsSamePortAs(in PortPath other);
    public Tri SharesControllerWith(in PortPath other);
    public Tri SharesRootPortWith(in PortPath other);
    public Tri SharesExternalHubWith(in PortPath other);  // D5
    public Tri IsDownstreamOf(in PortPath other);
}
```

**`Tri` is ADR-0078 D8's type, not a new one.** That ADR independently arrived
at `enum Tri { Unknown = 0, No, Yes }` for `SameContainer`, with the same
reasoning and the same ordinal-0 constraint. Two ADRs converging on one shape is
a reason to ship one type, not two spellings of it — so this ADR defines `Tri`
(it lands first) and ADR-0078 consumes it.

The exact C# spelling is left to the implementation, as ADR-0078 D8 allows.
What is **not** left open is the invariant it enforces: no member returns a
bare `bool`, a bare count, or a bare nullable that an unparsed value could
satisfy. D7 states why, and states what `default(PortPath)` is.

### D2. The grammar is platform-specific; the value is neutral; macOS is not a port path

Per ADR-0064, state the neutral contract and let each platform map its own
encoding:

| Platform | Source | Grammar | Status |
| --- | --- | --- | --- |
| Windows | resolved `LocationPath` | `#`-delimited segments, `USBROOT(n)` then `USB(n)`… | **measured, in scope** |
| Linux | sysfs syspath, already in `LocationPath`; or the `devpath` attribute | **nested directories**, one per hop: `…/usb9/9-3/9-3.1/9-3.1.1` | **measured, in scope** |
| macOS | — | **not a port path** | out of scope, permanently |

macOS's `LocationPath` is synthesized by Periphery as
`IOService:/{class}/{id}` (`MacOSDeviceProvider.cs:325`). It encodes no port
topology whatever. `TryParse` returns **false** there — which is the honest
answer, and is *not* the same as "zero hubs". The failure this rules out is the
one ADR-0078 D8 exists to prevent, and it is ruled out here by the same means:
a caller cannot reach a hub count without passing a `TryGet`.

**`TryParse` dispatches on the shape of the string, not on the host OS.** There
is no `OperatingSystem.IsWindows()` in the parser and no conditional
compilation. A Windows path is recognised by its `#`-delimited
`PCIROOT(…)`/`USBROOT(…)` shape; anything else fails to parse. This keeps the
core pure and total — the whole grammar is exercised from a Linux CI runner
against string literals, with no hardware and no platform gate — and it is why
adding the Linux grammar later is additive rather than a rewrite.

**The Linux grammar was inferred in the first draft, and the inference was
wrong in shape.** It claimed `…/usb1/1-2.1.3` — one segment carrying a
dot-separated port chain. Measured on the Linux device rig against a QEMU
nested-hub fixture, the syspath is **one directory per hop**:

```
/sys/devices/pci0000:00/0000:00:01.0/usb9/9-3/9-3.1/9-3.1.1
                                     ^root  ^hub  ^hub  ^device
```

Both forms carry the same information, but a parser written to the inferred
grammar would look for a segment that does not exist. Two consequences, and both
make Linux *easier* than this ADR assumed:

- **Ancestry is `dirname`, not string surgery.** The parent of `9-3.1.1` is
  `9-3.1`, a real directory. This is what ADR-0078 D8 relies on, and it is
  cheaper than that ADR claims.
- **The port chain is a first-class attribute.** `devpath` reads `3.1.1`
  directly, so the hub count needs no path parsing at all — read the attribute
  and count components.

Interface nodes appear as **children** of the device (`9-3.1.1:1.0`), which is
the Linux analogue of Windows' `USBMI(n)` — D3's trap in the other grammar. A
`dirname` walk handles them without a special case, where the Windows segment
scan needs one.

Two properties of the Windows side do still not transfer, and the parser must
not assume they do. `LinuxDeviceProvider` assigns `LocationPath = syspath`
**raw** (`LinuxDeviceProvider.cs:264`) — there is no `ResolveLocationPath`
equivalent normalising the input — so the Linux grammar has to locate the USB
chain inside an arbitrary syspath and reject non-USB ones
(`/sys/devices/virtual/…`, `/sys/devices/…/drm/card0`). `TryParse` returning
false for those is D7 doing its job, not a gap.

### D3. `USBROOT` and `USBMI` are not hub hops — the parser-level trap

The naive parse is *"split on `#`, count segments starting with `USB`."* It is
wrong twice over, because **three different segment kinds start with those
letters**:

- `USBROOT(0)` — the root hub. Counting it inflates every device by one.
- `USB(n)` — an actual hub-port hop. The only one that counts.
- `USBMI(n)` — a **multi-interface** segment: a composite device's interface,
  *below* the device, not a hub above it. Counting it inflates every composite
  device — and the motivating UPS is composite.

This is ADR-0078 D7's *"is the parent a hub?"* trap in a different costume: the
plausible one-liner is wrong in exactly the population that motivated the work.
The parser matches `USB(` exactly, and a test pins all three segment kinds.

**The trap is not confined to counting.** A trailing `USBMI(n)` corrupts the
*comparisons* in D5 just as badly, and in the same population. Discarding both
segment kinds at parse time (D1) is what stops the trap from having to be
caught twice; D5 records what it looks like when it is not.

**An unrecognised segment kind fails the whole parse.** `PCIROOT`, `PCI`,
`USBROOT`, `USB` and `USBMI` are the five kinds measured here; Windows
documents others (`ACPI`, `BTH`, …) that a different box would surface. The
permissive alternative — skip what you do not recognise and count the `USB(n)`
chain anyway — asserts that *unknown implies not-a-hub-hop*, and that is the
exact bet `USBMI` already won against. It was lost once, on a real device
class, and found only by measurement. Generalising it to every segment kind
nobody has looked at yet, with no measurement at all, is the same bet at longer
odds.

The trade is asymmetric in the direction this ADR always takes: strict costs a
**missing** answer on a box whose paths lead with a kind not seen here — legible
as such under D7, never readable as zero — while permissive costs a **silently
wrong** count. And strict is one additive line from correct the moment someone
measures such a box, whereas permissive gives no signal that it went wrong.

**Measured cost of strictness so far: zero.** The D4 probe run surfaced two kinds
this ADR had not seen — `ACPI(…)` and `UMB(…)`/`UMBROOT(…)`, across 52 rejected
paths — and **none of the 52 contained a `USBROOT` segment**. Every one would
have been rejected as "not a USB port path" regardless of the strict rule, so
strictness discarded no answer it could otherwise have given ([`portpath-parse-vs-devnode-walk-2026-08.md`](../explorations/portpath-parse-vs-devnode-walk-2026-08.md)).

### D4. External hub count is hop count minus one — and the cross-validation checks the parser, not the platform

The root hub contributes the first hop, so:

```
external hubs = Hops.Length == 0 ? 0 : Hops.Length - 1
```

Zero hops is the **root hub itself**, which genuinely has zero external hubs
above it. A directly-attached device (one hop) also has zero. Both are correct
answers to the consumer's question, and `TryGetIsRootHub` is what separates the
two cases when a caller needs them separated. `TryGetExternalHubCount`
therefore returns `false` for exactly one reason — the value is not a parsed
port path — which is what makes D7's rule exact.

**Validated against the devnode walk**: for every present `USB\VID_*` device on the
measured machine, the parsed count was compared with an independent walk of
`DEVPKEY_Device_Parent` that counts nodes named as hubs and stops at the root
hub. **42 devices, 42 agreements, 0 disagreements.**

**What that measurement is, and what it is not.** Both sides of the comparison
read the same cfgmgr32 devnode tree — Windows builds `LocationPath` by walking
it — so the agreement is not evidence that the enumerated tree reflects physical
topology. It cannot be, and this ADR does not need it to be: D8 scopes the count
to *"external hubs on the enumerated path"*, which makes the devnode tree the
authority the contract names rather than a proxy for the machine. Whether that
authority is itself faithful is D8's question, and D8's answer is that on
tunneled buses it is not.

So the claim is the narrower and more useful one: **the parser extracts from the
string exactly what an independent walk of the named authority yields**, for
every device measured. That is worth measuring because it is the failure that
would actually ship — D3's trap is a plausible one-liner away, and it is wrong
on composite devices in a way no amount of reading the code reveals.

**And the gap that leaves is closed on the other platform, which is why the two
measurements are worth more together than apart.** The formula generalizes to
Linux, and there it is cross-validated against *declared* ground truth. A QEMU
fixture was wired to a known topology — root hub → hub `h1` → hub `h2` → device
— and the guest's own view measured against it:

| sysfs node | `devpath` | components − 1 | actual hubs above |
| --- | --- | --- | --- |
| `9-1`, `9-2` (direct) | `1`, `2` | 0 | 0 ✓ |
| `9-3` (`h1`) | `3` | 0 | 0 ✓ |
| `9-3.1` (`h2`) | `3.1` | 1 | 1 ✓ |
| `9-3.1.1` (leaf) | `3.1.1` | 2 | 2 ✓ |

The topology here is whatever the QEMU arguments say it is, so this checks the
count against something **outside** the enumerated tree — exactly what the
Windows cross-validation structurally cannot do. Windows establishes that the
parser is faithful to the authority the contract names, at real scale and
against real composite hardware; Linux establishes that the *formula* is
faithful to a topology declared independently of any enumeration. Neither alone
would carry the decision. Together they cover both failure modes, and the
remaining exposure is the one D8 names: a bus whose enumeration hides hubs that
are physically present.

**It measured the easier half, and under the framing above that is exactly the
wrong half to have measured.** If the point is checking the parser against its
input, the population that matters is the one where the input is *synthesized*.
`USB\VID_*` devices are precisely the ones whose `LocationPath` comes straight
out of `DEVPKEY_Device_LocationPaths`. The population where the path is
*synthesized by Periphery's own ancestor walk* — `HID\…` function nodes, which
measure empty and are filled in by `ResolveLocationPath` — is **excluded from
the 42**. That is the population the Context section argues the whole approach
depends on, and it is where both motivating consumers live:
`Efm8HidProgrammer` opens a HID bootloader, and ADR-0078's measured UPS chain
begins at `HID\VID_0665&PID_5161&MI_01`.

So the re-run required before Accepted was:

1. The same parsed-vs-devnode-walk comparison over **`HID\*` nodes**, where the
   path is resolved rather than read.
2. The **`maxDepth: 8` exhaustion** and **no-ancestor-carries-a-port** cases,
   where `ResolveLocationPath` returns the bare `instanceId`. D7 requires those
   to not parse; nothing has measured that they don't.
3. The split of the 5 zero-hop devices into root hubs versus instance-id
   fallbacks.

**That re-run has now happened**, and its script, raw CSV and analysis are in
[`portpath-parse-vs-devnode-walk-2026-08.md`](../explorations/portpath-parse-vs-devnode-walk-2026-08.md) rather than surviving only as a number in this prose. Result, on
the Windows workstation, 300 devices:

| Plane | n | Agree | Disagree |
| --- | --- | --- | --- |
| `HID\*` — path *synthesized* by `ResolveLocationPath` | 21 | **21** | **0** |
| `USB\*` — path read from the OS | 54 | 49 | **0** |

All 21 `HID\` nodes had an empty own `DEVPKEY_Device_LocationPaths`, so all 21
paths came from the ancestor walk, which is the population that was missing. The
five `USB\` rows where the parser is conclusive and the walk is not are the five
root hubs: the walk looks for a root hub among the *ancestors* and terminates
`NoParent` when the node **is** one, where the parser answers `IsRootHub`. That
is the parser being more informative, not a disagreement.

Items 2 and 3 also came back clean: **56** instance-id fallbacks, of which
**0** parsed (D7 measured rather than asserted), and the zero-hop `5` splits
into **5 genuine root hubs** against **204** strings that are not port paths at
all. `ResolveLocationPath`'s `maxDepth: 8` turns out to have wide headroom — the
deepest real walk was **2** — so the exhaustion case cannot be produced by
plugging hardware in and stays a synthetic test against the `lookupNode` seam.

**The motivating shape is in the data.** One row exercises every property this
ADR leans on at once:

```
HID\VID_0763&PID_003A&MI_02\A&1D462EE9&0&0000
PCIROOT(20)#PCI(0301)#PCI(0000)#USBROOT(0)#USB(6)#USB(4)#USB(2)#USBMI(2)
```

A **composite** HID function node (hence the trailing `USBMI(2)`), behind **two
cascaded external hubs**, whose path was **synthesized** by `ResolveLocationPath`
from an ancestor at depth 1 because its own `DEVPKEY_Device_LocationPaths` is
empty. Parsed count 2; independent walk `ReachedRootHub` with 2; agrees. That is
structurally the ADR-0078 D7 a second host / UPS shape, and it is measured rather than
argued.

Two further runs deliberately changed the hub topology under it — relocating a
two-chip hub, then populating the cascade with a mouse and a Treehopper. `HID\`
came back 21 of 21 and then **28 of 28**, 0 disagreements throughout, so the
agreement is not an artefact of one arrangement. The cascaded-hub HID population
is now **8 rows across two vendors and two physically distinct hub chains**,
not one lucky device.

Run 3 also produced the `Efm8HidProgrammer` scenario in live data — two boards
with the **same VID/PID** at different depths under the **same root port**:

```
USB\VID_10C4&PID_8A7E\CDYHINBH   …#USBROOT(0)#USB(6)#USB(3)          → [6,3]
USB\VID_10C4&PID_8A7E\IMNUZ6YW   …#USBROOT(0)#USB(6)#USB(2)#USB(3)   → [6,2,3]
```

`SharesRootPortWith` = `Yes`, `SharesExternalHubWith` = `No`, `IsDownstreamOf` =
`No`. That is exactly the separation D5 exists to make and that ADR-0063's
whole-string equality cannot express — and it is also a live case where the
naive string prefix misfires, since `USB(6)#USB(2)` *is* a string prefix of
`USB(6)#USB(2)#USB(3)`.

Two later runs cascaded a third hub and then put a USB serial converter below
it, which produced the deepest case the machine can make:

```
FTDIBUS\VID_0403+PID_6001+FTGDS53GA\0000   ← path synthesized, ancestor depth 1
USB\VID_0403&PID_6001\FTGDS53G
  PCIROOT(20)#PCI(0301)#PCI(0000)#USBROOT(0)#USB(6)#USB(2)#USB(4)#USB(4)
  parsed 3 external hubs · independent walk 3 · agrees
```

**`hops - 1` is therefore exercised at 0, 1, 2 and 3 external hubs, agreeing
with the independent walk at every depth.** Those two rows are also D5's
position-is-not-identity case in live data: different devices, identical path,
`IsSamePortAs` = `Yes`.

One gap remains, recorded rather than smoothed over: every run is **one machine
on one Windows build**. Nothing here speaks to a different Windows version, a
Thunderbolt dock, or a box whose paths lead with a segment kind these five runs
did not surface — and D8 already records that a tunneled bus defeats the count
however well it is parsed.

### D5. Comparison is over the hop vector — and "controller", "root port" and "external hub" are three different comparisons

`ByLocationPath` (ADR-0063) compares whole strings, which answers *"is this the
same port?"* and nothing else. Parsed, the same data answers more — provided
the comparisons are done on the **parsed representation** and the granularities
are not conflated. Both are easy to get wrong.

| Question | Compare |
| --- | --- |
| same physical port | `Controller` equal **and** `Hops` equal |
| same **controller** / root hub | `Controller` equal |
| same **root port** | `Controller` equal and `Hops[0]` equal |
| same immediate **external hub** | `Controller` equal, `Hops.Length` equal **and ≥ 2**, and equal on all but the last |
| downstream of that hub | `Controller` equal and `Hops` a **proper prefix** — element-wise |

**These are vector operations, and stating them as string prefixes is wrong.**
The prose-level description — "prefix comparison" — invites a `StartsWith` on
the raw path, which fails two ways on measured data:

- **Multi-digit ports.** `…#USBROOT(0)#USB(2)` is a string prefix of
  `…#USBROOT(0)#USB(21)`. A string-prefix `IsDownstreamOf` reports that a
  device on root port 21 sits below a device on root port 2. The measured
  machine already carries `USB(8)`, and root hubs routinely expose more than
  nine ports.
- **Trailing `USBMI(n)`.** Take the composite path measured in the Context
  section and a sibling on the same external hub:

  ```
  PCIROOT(0)#PCI(0801)#PCI(0003)#USBROOT(0)#USB(1)#USB(2)#USBMI(0)
  PCIROOT(0)#PCI(0801)#PCI(0003)#USBROOT(0)#USB(1)#USB(3)
  ```

  They share an external hub. "Equal but for the last `USB(n)`" performed as
  string surgery leaves `…#USB(1)#USBMI(0)` against `…#USB(1)` and answers
  **no**. Over `Hops` — `[1,2]` against `[1,3]` — it answers yes, because
  `USBMI` was discarded at parse time and cannot come back.

Both failures land on composite devices and on busy hubs, which is to say on
the exact population that motivated the work. This is D3's trap a third time;
D1's representation is what retires it.

**Controller and root port are not the same comparison, and the machine
measured for this ADR contains the counterexample:**

```
PCIROOT(20)#PCI(0301)#PCI(0000)#USBROOT(0)#USB(2)
PCIROOT(20)#PCI(0301)#PCI(0000)#USBROOT(0)#USB(8)
```

Identical through `USBROOT(0)` — same controller, same root hub — and plugged
into **different root ports**. A `SharesRootPortWith` defined as "prefix through
`USBROOT(n)`" calls those two the same port, which is precisely the wrong answer
for `Efm8HidProgrammer`'s question: it exists to tell two boards apart when a
current collision hits one of them, and this would fuse them.

**The hub predicate is about *external* hubs, and the two-hop floor is why.**
Read naively — "equal on all but the last hop" — the pair above answers *yes*:
both devices hang off the root hub, so they share their immediate hub. That is
true, useless, and misleading in the same breath. It is redundant, because
`SharesControllerWith` already says everything true about that pair. And it is
misleading for the question `Efm8HidProgrammer` asks, which is whether two
boards contend for a shared resource — a hub's upstream port and its power
budget. The root hub is not that resource; an external hub is. So the predicate
is named `SharesExternalHubWith`, it requires at least two hops, and two devices
on different root ports answer **no**. (Nothing is being renamed: no such member
exists yet. An earlier draft of this ADR proposed `SharesImmediateHubWith`, and
this is the correction of that draft, not a migration.)

That also keeps D5 consistent with D4, which counts *external* hubs and would
otherwise report zero for a pair the hub predicate called hub-sharing.

**This is ADR-0078 D7's argument, applied to a comparison instead of a
predicate.** D7 rejects *"is the parent a hub?"* partly because for a
directly-attached device it answers yes — *"the parent is a hub, the root one —
which reads as 'there is a hub in the way' when there is not."* A hub predicate
that answers yes for two devices on separate root ports makes the identical
mistake in the identical population, one abstraction later. The two ADRs agree,
and D5 without the two-hop floor would have been the place this ADR disagreed
with 0078 by accident.

**They still do not form a ladder — the two-hop floor relocates the break
rather than removing it.** Each relation carries a **depth precondition**:
`SharesRootPortWith` needs at least one hop on both sides, `SharesExternalHubWith`
at least two. Where the precondition fails the answer is `No`, which is correct —
two devices cannot share a resource that is not in either path — but it means the
implications are conditional:

- `IsSamePortAs` does **not** imply `SharesExternalHubWith`. Two devices at the
  identical one-hop port share that port exactly and have no external hub between
  them and the root hub.
- `IsSamePortAs` does **not** imply `SharesRootPortWith` either, for two root hubs
  on one controller: both have an empty hop vector, so there is no `Hops[0]` to
  agree on.
- `IsDownstreamOf` does not imply `SharesRootPortWith` when the upper operand *is*
  the root hub, for the same reason.

Among pairs deep enough for every precondition to hold — two hops or more —
`IsSamePortAs` ⇒ `SharesExternalHubWith` ⇒ `SharesRootPortWith` ⇒
`SharesControllerWith` does hold, because equality on all but the last of ≥ 2
hops forces `Hops[0]` equal. That is a **conditional invariant, not a total
order**, and it is the reason these stay five predicates instead of one ranked
enum: a rank would have to claim an ordering that the shallow cases falsify.
`IsDownstreamOf` is disjoint from `SharesExternalHubWith` unconditionally, since
a proper prefix cannot have the same length as the vector it prefixes.

The shallow cases are where a ranked answer would quietly mislead, so each of the
three bullets above is its own test.

A `PortPath` is a **position, not a device identity.** Because
`ResolveLocationPath` hands a function node its ancestor's path, a
non-composite USB node and its `HID\` child resolve to the *identical* path.
`IsSamePortAs` returning `Yes` means "the same physical port", which is a true
statement about two distinct devices in that case. No caller should read it as
"the same device."

ADR-0063's correlation is re-expressible as `IsSamePortAs`. **Whether to
re-express it is deliberately not decided here** — it works, and churning a
shipped bootloader path for tidiness is not a reason. Note if it is ever done
that `DeviceWaitState.Correlates` compares `OrdinalIgnoreCase`
(`DeviceWaitState.cs:264`) while a parsed comparison is ordinal over a
controller string and integer hops, so the swap is a small behaviour change
rather than a pure refactor.

### D6. The first call site is chosen now, and the design is tested against it

`Efm8HidProgrammer.OpenAsync` (`Efm8HidProgrammer.cs:70`) currently logs
`LocationPath`, `ParentId` and `PortNumber` as raw strings, with a comment
saying the point is to let someone see *"whether they share a hub or root
port"* when two boards fail concurrently. That correlation is done by a human
diffing two log lines.

**That log line is where `PortPath` lands first: it will emit the parsed
relation alongside the raw strings.** The hub count and the root-port
comparison are exactly what the comment says the operator is trying to extract,
and they are one `TryParse` away at a site that already holds the string.

**Nothing in `src/` has changed yet, and this ADR does not claim otherwise.**
No `PortPath` type is implemented and `Efm8HidProgrammer` is untouched; what is
decided here is *which* call site adopts it and *what* it emits, so that the
implementation has a defined first consumer instead of arriving speculatively.
An ADR records a decision; the code follows it.

This is deliberately the smallest possible adoption: one log statement, no
behaviour change, no new IO, nothing on the flashing path. **It is not a claim
that the type has a consumer** — nothing will *depend* on the relation, and by
ADR-0078's own rule-of-three standard the count stays zero. It is here because
it is a **forcing function on the API**.

Choosing that line is the first time anyone has to pick between
`SharesRootPortWith` and `SharesExternalHubWith` with a real question behind the
choice, and the choice is not obvious until you try: D5's two-hop floor exists
because working through this call site exposed that the naive hub predicate
answers *yes* for two boards on separate root ports. An API with no call site in
mind does not find that. Naming one is cheap insurance against shipping a
surface that reads well in an ADR and answers the wrong question in the field.

The raw strings stay in the log alongside the parsed relation — the parse is an
addition to the evidence, not a replacement for it, which is the same posture
ADR-0073 D1 takes.

`ByLocationPath` (D5) and the prospective UPS check are explicitly *not* in
scope here.

### D7. A path that does not parse is a state, never a zero — and `default` is that state

`TryParse` returning false means *this string is not a port path* — a
non-USB device, a macOS synthetic path, an instance-id fallback from
`ResolveLocationPath` when no ancestor carried a port. None of those is "zero
hubs", and none may be silently readable as one.

**C# cannot stop a `readonly struct` from being default-constructed, so the
ADR must say what `default(PortPath)` means rather than pretend `TryParse` is
the only door.** `PortPath p = default;` is legal, arises naturally from an
uninitialized field or an array element, and skips parsing entirely. So:

> **`default(PortPath)` is the unparsed state, and it is indistinguishable at
> the API from a string that failed to parse.**

That is what forces the shapes in D1, and it is why ADR-0078 D8's rule needs
restating here rather than merely citing:

- **`TryGetExternalHubCount` / `TryGetIsRootHub` return `false` on an unparsed
  value.** The payload is unreachable. A bare `bool IsRootHub` would fail this
  on its own terms — `false` and "this is not a port path" would be the same
  bool and opposite facts, which is precisely the failure this decision names.
- **The five relations return `Tri.Unknown`, never `No`.** A bare
  `bool` here is not safe either, and the reason is worth being explicit about,
  because it is less obvious than the count case: `SharesRootPortWith`
  returning `false` on an unparsed operand would tell `Efm8HidProgrammer` that
  two boards are on *different* root ports when the truth is that it cannot
  see. That is a confident wrong answer produced by a missing measurement —
  the exact failure mode D8 and ADR-0078 D7 both exist to prevent.
  `Tri.Unknown = 0` is the zero value for the same reason
  `MonitorLayoutAvailability.NotMeasured` is (ADR-0073 D4): the default must be
  the honest answer, not the negative one.

**The gate and the enum are not a strong guard and a weak one — they guard
different things, and the ADR should not pretend otherwise.** ADR-0078 D8's
rule is a rule about *payloads*: do not let a value be read without passing the
state that says whether it means anything. `TryGetExternalHubCount` has a
payload — an `int` living inside the value — and the `out` parameter genuinely
makes it unreachable. A relation has no payload. It is a total function of two
values, and its answer *is* the state, so there is nothing to put behind a gate;
three-valued is simply the right shape, and it is the shape ADR-0073 D4 chose
for `MonitorLayoutAvailability` for the same reason.

What neither shape does is stop a caller who flattens on purpose.
`x.SharesRootPortWith(y) != Tri.Yes` collapses `Unknown` into `No`, and
`if (!p.TryGetExternalHubCount(out var c)) c = 0;` collapses exactly as hard one
line wider. C# offers no construction that forbids either. What both shapes buy
is that the collapse has to be **written down at the call site**, where review
and `grep` can see it — and that is the whole of the guarantee. Stating it that
way is better than implying an enforcement the language cannot deliver.

A test pins every member against `default(PortPath)`.

### D8. `PortPath` says what the path is, never what it means

The same boundary ADR-0078 D9 draws. `PortPath` reports two external hubs; it
does not report whether a UPS behind them can be trusted during a mains outage.

And the limitation ADR-0078 records applies here **unchanged and for the same
reason**: on tunneled or redirected buses (USB4/Thunderbolt docks, usbipd,
VMBus) the enumerated path does not include hubs that are physically in the
power path, so a count of zero can be *wrong* rather than absent. Parsing a
string does not fix a lossy projection — it inherits it. The count is documented
as *"external hubs on the enumerated path."*

## Consequences

- **Two of ADR-0078's three consumers become answerable without a graph** —
  *answerable*, not answered, and not yet implemented. This ADR adds no `src/`
  code: it decides the shape of `PortPath`, the grammars it parses, and the one
  call site that adopts it first (D6). Nothing depends on the relation, the UPS
  check has no code yet, and `ByLocationPath` is left alone on purpose. When it
  is implemented it needs no new IO, no new enumeration, no new provider work
  and no termination states.
- **`ResolveLocationPath` becomes more valuable, not less.** It is what makes
  the path present on function nodes, so this ADR raises the return on a walk
  the library already maintains — and correspondingly makes that walk's
  correctness part of this ADR's evidence burden (D4).
- **ADR-0078 is not obsoleted, and is narrowed.** Questions this cannot answer —
  arbitrary ancestry, descendants, container grouping, cycle and completeness
  reporting, non-USB topology — remain exactly the case for a graph. What
  changes is that the graph no longer has to justify itself on the hub-count
  consumer, which was its weakest support.
- **A string-shaped structural truth is now a parsed value**, which is the
  objection ADR-0078 raised against this approach and declined to substantiate.
  The answer is that the string is not being trusted — it is being *parsed*,
  *typed*, and *checked against the authority its own contract names* (D4).
  That is a narrower claim than "cross-validated against the topology", and D4
  now says so rather than rounding it up.
- **`PortPath` is Windows-measured, Linux-measured, macOS-absent.** Linux was
  inferred in the first draft, inferred *wrongly in shape*, and is now measured
  against a declared QEMU topology (D2, D4). macOS is out by construction. The
  type is honest about the remaining gap at every call site rather than in a
  docs paragraph.

## Open questions

- **~~The `HID\*` validation re-run (D4) is the blocking item.~~ Discharged** —
  five runs across four topology changes, 0 disagreements throughout, on the
  population whose path `ResolveLocationPath` synthesizes; the count agrees at
  0, 1, 2 and 3 external hubs, and the composite-behind-hubs shape spans two
  vendors and two hub chains ([`portpath-parse-vs-devnode-walk-2026-08.md`](../explorations/portpath-parse-vs-devnode-walk-2026-08.md)). What replaces it is a breadth
  limit, not an open question: **one machine, one Windows build**.
- **Does `USBMI` ever nest?** Still open; no nesting seen on either machine.
  The neighbouring question — which segment kinds a different box surfaces — is
  partly answered: the D4 run turned up `ACPI(…)` and `UMB(…)`/`UMBROOT(…)`, and
  neither appeared on a path that also had a `USBROOT`, so D3's strict rule has
  so far cost nothing. It remains a *measurement* question rather than a design
  one, since each new kind is an additive change with a test.
- **Non-USB and alternate controllers are unmeasured on Linux.** The syspath
  grammar is measured (D2) and the hub-count formula cross-validated (D4), both
  against a QEMU xHCI tree. What has *not* been checked is whether other
  controller types on Linux — or USB behind a non-PCI bus — produce the same
  nesting shape. macOS remains absent entirely, and is now the only wholly
  unmeasured platform.
- **Should `PortPath` be surfaced on `DeviceInfo`, or constructed by callers?**
  A parsed property would be computed for every device on every enumeration to
  serve a minority of callers — the same cost objection ADR-0073 D3 raised
  against duplicating device-tree facts. Lazy construction from the string is
  the cheap default, and D1's static `TryParse` assumes it.
- **Does this subsume ADR-0078 for the currently demonstrated need?** This ADR
  claims only that it serves two consumers. Whether the third — and the
  graph-shaped questions nobody has yet asked — justify ADR-0078 alongside it is
  that ADR's question, not this one's.

---
title: "ADR-0078: Device topology is a rooted forest, not a DAG — and the forest is a snapshot, not the machine"
status: "Rejected"
status_note: "analysis retained; D10 split to ADR-0080"
date: "2026-08-22"
authors: "@charles8051"
tags: ["architecture", "decision", "topology", "device-tree", "usb", "hub", "windows", "linux", "macos", "contract", "functional-core"]
split_to: "0080-ancestor-walking-is-one-fold.md"
supersedes: ""
supersedes_note: "proposed to supersede ADR-0002 Decision 3; rejected, so D3 stands and no supersession took effect"
superseded_by: ""
---

# ADR-0078: Device topology is a rooted forest

## Status

**Rejected 2026-08-24. The reject trigger this ADR wrote for itself fired:**
*"the trigger to reject it is ADR-0079 shipping and nobody asking."* ADR-0079 is
Accepted and implemented on the `topology` trunk, and no consumer has asked for
descendants, container grouping, or ancestry across a non-USB plane.

**ADR-0002 D3 therefore stands.** The reversal this ADR proposed does not
happen, and no `DeviceTopology` type is added to core.

### Why

Three capabilities survived D11's narrowing. Each has a cheaper answer today:

| Capability | Nearest real use case | Cheaper answer that already exists |
| --- | --- | --- |
| Descendants | which devices go away when a hub is reset (ADR-0060, ADR-0075) | `PortPath.IsDownstreamOf` over the enumerated list, an O(n) filter with no graph |
| Container grouping | pair a webcam's UVC video node with its UAC audio node | `ContainerId` equality, a group-by. D2 already keeps this a separate relation from parentage |
| Non-USB ancestry | which GPU drives this monitor | DisplayConfig adapter ids, not devnode ancestry |

Roots, cycle classification and completeness reporting are hygiene for the graph
itself rather than consumer value. Nothing outside the graph asks for them.

**The cross-platform half was never costed honestly.** The Context table
measures `ParentId`, `PortNumber` and `ContainerId` as Windows-only, and the
Linux rig returned 785 devices with 0 parents. Accepting this ADR would not have
been accepting a graph. It would have been committing to a sysfs ancestry
backend and an IOKit plane traversal, neither written and neither validated.
ADR-0079 crossed that line for one narrow question against a declared QEMU
topology, and that was the expensive part.

### What survives

**D10 is split out to [ADR-0080](0080-ancestor-walking-is-one-fold.md) and is
not rejected with the rest of this document.** Four independently written
ancestor walks with three different bounds is a real defect in shipped code, and
D10's own finding is that consolidating them is the *walker's* job rather than
the graph's. That work depends on no decision below.

**The rest of the document is retained rather than deleted.** D4's termination
taxonomy, D5's cycle-classification-over-bounds argument, D6's case against a
depth bound, and D7's hub-predicate analysis are the reusable parts, and
ADR-0080 cites them rather than restating them. The measurements — the Linux
`SUBSYSTEM` cut, the `HTREE\ROOT\0` finding, the 785-device probe — are
evidence about the platform and remain true whatever happened to this decision.

**The revisit trigger is the one the open questions already name**: a consumer
that asks for descendants across a plane `PortPath` does not parse, for
container grouping as a tree rather than a group-by, or for ancestry on Linux or
macOS. Write a new ADR at that point rather than editing this one.

### Original status text

Reverses ADR-0002 Decision 3. **ADR-0079 lands first** and serves two of the
three consumers below with a parser; this ADR is narrowed to what a parser
cannot do, and stays Proposed until one of those questions has a consumer
(D11). Carries the posture
ADR-0068 set for rotation and ADR-0073 D4 restated for layout (*unmeasured is
its own state, never a negative result*), and ADR-0073 D1 for the
evidence/verdict line: **Periphery reports what it observed; the consumer forms
the verdict.**

## Context

`DeviceInfo.ParentId` has existed since ADR-0002. What has never existed is a
way to *ask a topology question*. There is no ancestors query, no descendants
query, no root, no depth, and no hub predicate — `grep` for `Ancestors`,
`Descendants` or `IsHub` across `src/` returns nothing. `DeviceFilter` offers
exactly one *ancestry* operation, `WithParent(parentId)`, which selects
one-level **children**. The upward direction is the asymmetric gap.

ADR-0002 D3 ruled this out deliberately, and the reasoning was sound at the
time:

> *"No tree-building helper in core. A materialized `DeviceTreeNode` graph is a
> consumer convenience. It belongs in `Periphery.Examples` or a future utility
> package, not the core library."*

Alongside it, D2 justified omitting `Depth` as *"trivially computable by walking
`ParentId`."* That claim is the one this ADR tests, and the library's own code
has already falsified it.

### The library built the abstraction privately — four times, with three different bounds

`WindowsDeviceProvider.ResolveLocationPath`
(`src/Periphery/Windows/WindowsDeviceProvider.cs:241`) is an ancestor walk:
`internal static`, bounded by `maxDepth: 8` so *"a broken/cyclic chain can never
loop"*, taking a `lookupNode` seam so the IO stays in the shell, and unit-tested
against a fake chain with no hardware
(`tests/Periphery.Tests/Platform/WindowsDeviceProviderTests.cs`, seven cases —
including the cyclic one and the missing-parent one). Its own remarks call the
walk *"a pure fold over the chain so it is unit-testable without hardware."*

It is also **not the only one**. The library ships four ancestor walks today,
each written independently:

| Walk | Bound | Walks |
| --- | --- | --- |
| `WindowsDeviceProvider.ResolveLocationPath` | 8 | live cfgmgr nodes |
| `WindowsDeviceReset.TryResolveUsbAncestor` (`:217`, `MaxAncestorWalk` `:61`) | **16** | live cfgmgr via `CM_Get_Parent` |
| `LinuxHidBackend.ResolveDevNode` (`:403`) | 8 | sysfs path ascent |
| `LibUsbBackend.ResolveDevNode` (`:770`, loop `:790`) | 8 | sysfs path ascent |

Four implementations, **three different bounds**, no shared tests. The
"trivially computable" walk needed a depth bound, a lookup seam, a missing-node
fallback and seven tests — and then needed them again, three more times, with
nobody noticing the repo had already picked 16 for a structurally identical
traversal. That is what a missing library function looks like.

**But note what kind of walk they all are**, because it constrains the design
below: every one resolves against **live OS handles**, and every one
deliberately traverses nodes no snapshot contains. `LookupNodeForLocation`
(`WindowsDeviceProvider.cs:266`) locates an arbitrary instance id with no class
filter at all. See the open question on shell-side walks.

### Rule of three

| Consumer | What it needs | What it has |
| --- | --- | --- |
| `ResolveLocationPath` | first ancestor carrying a port | its own private bounded walk |
| `Efm8HidProgrammer` (`src/Periphery.Bootloader.Efm8.Usb/Efm8HidProgrammer.cs:76`) | *"whether two boards share a hub or root port"* | logs `ParentId` as a string and leaves the correlation to a human reading logs |
| the kiosk UPS check (prospective) | how many **external** hubs sit between a UPS and the root hub | nothing |

Three independent reaches for the same missing abstraction. The second is the
telling one: it wants a topological *relation* and settles for printing an
opaque id — alongside `LocationPath` and `PortNumber`, which are the raw
material of the answer — because the *relation* over them is not expressible.

### What is actually populated today — verified, not assumed

| Field | Windows | Linux (libudev) | macOS (IOKit) |
| --- | --- | --- | --- |
| `ParentId` | ✅ `DEVPKEY_Device_Parent` | ❌ never set | ❌ never set |
| `PortNumber` | ✅ `DEVPKEY_Device_Address` | ❌ never set | ❌ never set |
| `ContainerId` | ✅ `DEVPKEY_Device_ContainerId` | ❌ never set | ❌ never set |
| `LocationPath` | ✅ resolved port path | ⚠️ raw sysfs syspath | ⚠️ synthesized `IOService:/{class}/{id}` |

Both relations this ADR is about are Windows-only, and both are **silently
null** elsewhere — indistinguishable from a genuine root or a genuine
singleton. That is wider than the gap the proposal named, and it is the part
most likely to produce a wrong answer rather than a missing one.

The table above is read from the providers. Confirmed at runtime on the Linux rig
(2026-08-23) rather than inferred from them: enumerating with `DeviceCategory.All`
returned **785 devices, 0 with a non-null `ParentId`**. So the Linux column is not
"populated but sparsely" — the relation is absent for every device on the machine, which
is what makes D8's *unmeasured is its own state* load-bearing rather than defensive. The
probe and its exact invocation are recorded in
[`docs/explorations/device-emulation-and-graph-walking-2026-08.md`](../explorations/device-emulation-and-graph-walking-2026-08.md).

## Decision

> ### Everything from here to the end of this document is historical
>
> **This ADR was Rejected. D1 through D9 and D11, and the Consequences section,
> describe a decision that was never taken and a type that does not exist.**
> They are retained as analysis, not as architecture. Where the text below says
> a thing "is" decided, read "was proposed". In particular:
>
> - **ADR-0002 D3 was not reversed.** The Consequences section says it was. That
>   sentence describes the proposal, and the proposal was rejected.
> - **No `DeviceTopology` type, no `Ancestors`, `Descendants`, `Root`, `Depth`,
>   `SameContainer` or termination enum was added to `src/`.**
> - **D10 is the exception.** It was split out and is live as
>   [ADR-0080](0080-ancestor-walking-is-one-fold.md). It carries its own
>   historical marker below.
>
> The **measurements** are the part that stays true: the Linux `SUBSYSTEM` cut,
> the `HTREE\ROOT\0` finding, the 785-devices-with-0-parents probe, and the
> four-walks-three-bounds survey are observations about the platform and the
> repo, and ADR-0079 and ADR-0080 cite them. See the Status section for why the
> decision they were gathered for did not survive.

### D1. It is a rooted forest. Not a DAG, and the distinction is the point

**Every devnode has at most one parent** — out-degree in the parent direction is
never greater than one. Roots have none; D4 defines which nodes are eligible.

Strictly, that makes the structure a **partial functional graph**, and a *forest*
only when it is also acyclic — which D5 declines to assume and provisions a
`Cycle` state for. "Rooted forest" is the shape this models on well-formed
input, not an invariant the library may rely on. Where the distinction bites is
D2's algebra, which is stated there.

A measured chain from a production kiosk:

```
HID\VID_0665&PID_5161&MI_01\…
  → USB Composite Device
    → Generic USB Hub
      → Generic USB Hub
        → USB Root Hub (USB 3.0)
          → Intel USB 3.0 eXtensible Host Controller
```

There are no diamonds and no multi-parent merges, and therefore no
topological-sort problem, no "which path to the root" ambiguity, and no
reachability query that needs a visited set to terminate.

Modelling this as a general graph would import generality the domain does not
have, and the cost is paid in the query signatures. On a forest every useful
query has **one** answer:

| Query | Forest | General DAG |
| --- | --- | --- |
| ancestors | one ordered list | a set of paths |
| root | one value | a set of roots per node |
| depth | one number | a range |
| lowest common ancestor | one node, **or none if the two are in different trees** | may not exist, and may be several |

The last row is the one exception, and it is a property of the *forest*, not of
the snapshot: two nodes under different roots genuinely have no common ancestor.
So an LCA query would have to return an optional — which is why **LCA is not in
the proposed surface** (D3, D7 and D8 list what is). It appears only to make the
contrast with a DAG honest, where the answer is not merely optional but
plural.

A DAG-shaped API would make each of the others return a collection, and every
consumer would then write the code that collapses it back to the single answer
the hardware guarantees. **Generality that every caller has to undo is a defect,
not a feature.**

**How much work is this contrast really doing?** Less than its prominence
suggests, and that is worth recording rather than hiding. Every signature above
falls out of *at most one parent* — a property of `DeviceInfo.ParentId` being a
single field — not out of acyclicity. Acyclicity, the actual forest/functional-graph
distinction, changes **no signature at all**: D5 admits cycles and every
signature survives. The one genuinely discriminating row is LCA, which is not in
the proposed surface. So D1 is best read as *"the record already constrains this
to one parent, so do not design as if it did not"* — a caution against imported
generality, not a discovery. Whether it earns its length is an open question
below.

Note that the 29-root measurement below settles a **different** claim: it
establishes *forest rather than tree*, which is load-bearing (a design assuming
one root is wrong on the default query). It does not rescue *forest rather than
DAG*, which remains the weaker half of this decision.

ADR-0002 D5 already recorded the underlying fact — *"No shipping bus protocol
uses mesh topology"* — without drawing the API consequence from it. This is
that consequence.

**Forest, not tree — but not for the obvious reason.** The tempting
justification is "a machine with two host controllers has two roots," and it is
**measurably wrong on Windows**: a controller's parent is a PCI Express Root
Port, and every chain converges. Measured on an AMD/Renesas multi-controller
box:

```
USB\ROOT_HUB30\…  →  PCI\…&DEV_148C (xHCI)  →  PCI\…&DEV_1484 (PCIe Root Port)
  →  ACPI\PNP0A08\0  →  ACPI_HAL\PNP0C08\0  →  ROOT\ACPI_HAL\0000  →  HTREE\ROOT\0
```

`HTREE\ROOT\0` has no parent. Followed to termination, the raw Windows device
tree is a **tree with a single synthetic root**.

**The forest is nonetheless what Periphery models, and this is measured rather
than argued.** Once that synthetic root is normalized away (D3), a
`DeviceCategory.All` snapshot on the same machine has **29 roots** — every
`ROOT\…` and `SWD\…` devnode that hung directly off it:

```
ROOT\ACPI_HAL\0000   ROOT\SYSTEM\0000    ROOT\VOLMGR\0000    ROOT\UMBUS\0000
ROOT\COMPOSITEBUS\0000   ROOT\BASICDISPLAY\0000   SWD\PRINTENUM\PRINTQUEUES   … (29 total)
```

So no single node is "the root", and a design that assumed one would be wrong on
the default query on the only platform that populates parentage. The forest is
not a hedge against exotic hardware; it is the ordinary shape of the ordinary
snapshot.

### D2. Parentage and container identity stay two relations, not one graph

They are different kinds of mathematical object, and fusing them loses that:

- **Parentage** generates an **ancestor** relation that is transitive, and on
  acyclic input also irreflexive and antisymmetric — a strict partial order.
  (`parent-of` itself is not transitive; `ancestor-of` is its transitive
  closure.) On a `Cycle` component neither property holds: a node on the cycle
  is its own ancestor. Consumers must not assume `Ancestors(x)` excludes `x`
  without checking the termination. It answers *"what is this plugged into?"*
- **Container identity** (`DeviceInfo.ContainerId`) is an **equivalence
  relation** — reflexive, symmetric, transitive. It answers *"which devnodes are
  the same physical box?"* A composite UPS's `MI_00` and `MI_01` interfaces are
  one device with one power cord.

Fusing them into a single graph with two edge kinds would make `Parent()`
ambiguous — a caller would have to know which edge kind it is following, and the
ancestors query would stop being total the moment a sibling edge could be
traversed. Keeping them separate means each relation carries exactly its own
algebra: ancestry is walked, container identity is *grouped*.

**Equivalence is asserted only over known ids.** An unknown container id is not
a container. `SameContainer(a, b)` returns a three-valued answer — `Yes`, `No`,
`Unknown` — and **`Unknown` is returned whenever either side's `ContainerId` is
absent, never `Yes`**. Two devices that both lack a container id are not thereby
the same box; on Linux and macOS today, *every* device lacks one, so a boolean
`SameContainer` would report the entire machine as one physical device. Grouping
APIs (`GroupByContainer`) place unknown-container devices in **no group** rather
than in a shared null group, for the same reason.

### D3. An immutable graph value, built once, with pure total queries over it

Per the repo's functional-core / imperative-shell directive:

- **Shell** — enumeration. `DeviceQuery.ToListAsync()` with `DeviceCategory.All`
  already produces the full device list, so the id → `DeviceInfo` map is
  constructible today with no new IO.
- **Core** — an immutable value built from that list, with pure total queries:
  ancestors, children, descendants, root, depth, same-container. Same input,
  same output, no clock, no `Task`, no handle.

**The id→`DeviceInfo` map must key on `DeviceId`, not on `string`.** This looks like a
detail and is a correctness constraint on the join D3 is built from.
[#231](https://github.com/charles8051/periphery/issues/231) records instance ids arriving
with different case across re-enumerations (`CDYHINBH` → `cDYhINBh`). `DeviceId` already
absorbs that — `Equals` and `GetHashCode` both use `OrdinalIgnoreCase` — so a
`DeviceId`-keyed map is correct by construction. A raw-string map, or one built with the
default comparer, is not: a case-varied parent id would fail to resolve, and **that failure
would not look like an error**. It would surface through D4/D8 as a legitimately named state
— filtered, or phantom — because those states exist precisely to describe a parent id that
resolves to nothing. A wrong answer wearing the costume of a correct one is this ADR's own
stated failure mode, and here it would be introduced by a `Dictionary<string, …>`.

**The input is a snapshot *plus its provenance*, not a bare list.** This is
forced, not decorative. A bare `IReadOnlyList<DeviceInfo>` cannot support the
states D4 promises: from the list alone, a parent id that resolves to nothing is
observationally identical whether the filter excluded it, the provider never
returned it, or it names a phantom — and an empty child set is identical whether
the hub is empty or its children were filtered out. **Completeness is not
derivable from a list; it is a fact about the query that produced it.**

So construction takes the device list *and* a `SnapshotProvenance`. But
provenance must be the **right** data, and naming the filter is not enough: the
core cannot evaluate a `DeviceFilter` against a parent it does not have, so
"would this filter have admitted the missing node?" is unanswerable from inside.
Provenance therefore carries **conclusions the shell drew**, not inputs for the
core to re-derive:

- **`SubtreeClosure`** — did this enumeration cover everything reachable
  below any node in it? Only a scope known to be closed under descendants
  (today: `DeviceCategory.All`, run to completion) can claim it. A completed
  *category* query cannot: it legitimately omits subtree nodes of other
  classes. **Downward completeness is claimed only from this guarantee**, and
  is otherwise inconclusive — a completed query is not a closed one, and
  conflating them is what makes an empty child set look authoritative.
- **`ParentPresence`** — for each unresolved parent id, whatever the shell
  established about it (see D4). Absent this, the core cannot say *why* a parent
  is missing.

What the core decides from the list alone is only the **structural** question:
`ParentId` null → `Root`; `ParentId` set but not in the map → `LeftSnapshot`.

**With one normalization, which the provider owns.** A parent id naming a known
**synthetic platform root** is treated as *no parent* — `Root`, not
`LeftSnapshot` — because such a node is not a device and can never be a
meaningful ancestor. Without this, a top-level device whose parent is
`HTREE\ROOT\0` reports "my parent is elsewhere" when the truth is "I am
top-level", and on a category query the whole graph can end up with no `Root` at
all. Worse, `ParentPresence` probing makes it *more* wrong, not less: the
synthetic node genuinely exists, so it would resolve as present and pin the
device at `LeftSnapshot` forever.

Which ids are synthetic is **platform knowledge** — `HTREE\ROOT\0` on Windows,
and the analogous roots on the other two — so it belongs behind the provider for
exactly the reason D7 gives for hub predicates.

**Normalizing the edge is not sufficient on its own.** The synthetic devnode is
itself enumerated — Periphery's `All` path is
`SetupDiGetClassDevs(null, DIGCF_PRESENT | DIGCF_ALLCLASSES)`
(`DevNodeHelper.cs:493`) and the loop skips nothing — and it has a null parent,
so it would arrive as a `Root` in its own right: a non-device sitting in the
root set, in ancestor lists and in depth counts. So the same platform knowledge
**excludes it as a node**, not only as a parent. A record that names no device
is not a node of a device topology.

Whether such a record should be enumerated *at all* is a separate question this
ADR does not reopen; it only declines to give it a place in the graph.
That much is honest with no provenance at all, and a graph built from a bare
list still constructs — reporting every unresolved parent `LeftSnapshot` and
every child set possibly-incomplete. **`LeftSnapshot` therefore means "named, and
not here," not "excluded by the filter."** Narrowing it further is a shell
judgement the core reports rather than makes.

This is exactly the split `ResolveLocationPath` already demonstrates, tested the
way its walk is already tested — against a fake chain, with no hardware. The
existing tests are the template, not an analogy.

**The type lives in core `Periphery`.** ADR-0002 D3 placed it outside on the
"consumer convenience" premise this ADR reverses, and the remaining constraint —
`docs/ARCHITECTURE.md`'s discovery-only core — is about *IO*, which a pure value
transform over an already-enumerated list does not perform. A separate package
for a type with no dependencies and no IO would be ceremony.

**Rebuild is the only lifecycle.** The graph is a **snapshot**, and naming it so
matters: it is built from a device list taken at a moment and it does not track
arrivals. A `DeviceWatcher`-driven incremental update would fuse state and
timing into the value D3 exists to keep pure, so a stale graph is discarded and
rebuilt rather than patched. Revisit if a long-running consumer measures the
rebuild cost and finds it real. ADR-0002 D2 declined to
put `Depth` on `DeviceInfo` precisely because a field *"could go stale if the
tree is re-enumerated"* — that reasoning survives intact here. Depth belongs to
the graph value, which is honestly a snapshot, not to the record, which presents
itself as current.

### D4. The snapshot is a *cut* of the device tree, so `root` is snapshot-relative

What `root` means is undefined until this is settled, and `depth`, ancestor
termination and hub counting all rest on it — so it is settled here rather than
deferred. The rules it carries downstream are decided separately: cycles in D5,
the absence of a depth bound in D6, hub counting in D7.

**The synthetic root is in the snapshot, and this ADR previously claimed the
opposite.** The appealing argument was that `HTREE\ROOT\0` carries no class GUID
while Periphery enumerates by class, so the one node corresponding to no device
could never enter the model. Measured directly against the API the provider
actually calls — `SetupDiGetClassDevs(null, DIGCF_PRESENT | DIGCF_ALLCLASSES)`,
which is the path `DeviceCategory.All` takes (`WindowsDeviceProvider.cs:44` →
`DevNodeHelper.cs:493`) — that is **false**:

| Node | Class GUID | Returned by `DIGCF_ALLCLASSES` |
| --- | --- | --- |
| `ROOT\ACPI_HAL\0000` | `{4D36E966-…}` (Computer) | yes |
| `ACPI_HAL\PNP0C08\0` | `{4D36E97D-…}` (System) | yes |
| **`HTREE\ROOT\0`** | **none** | **yes — 1 of 301 devnodes** |

The class-GUID filter is pushed down only for a *category* query; `All` passes a
null GUID and takes the all-classes path, and the enumeration loop skips nothing
(`WindowsDeviceProvider.cs:89–122` drops a node only when `ToDeviceInfo`
throws). So the synthetic node arrives in the graph unless something removes it,
which is why D3 normalizes both the edge into it and the node itself.

That is worth stating plainly because it is the horn this ADR previously claimed
to have escaped, on reasoning that measurement then falsified. It does not
rescue the design — it **is** the design's load: the model must tolerate
whatever the enumeration hands it, which is precisely why `Root` is defined
below as a property of the snapshot rather than a claim about the machine.

The cut is real in the other direction too, and there it is unavoidable: under
any *category* query the chain leaves the snapshot long before any of this —
a `DeviceCategory.Usb` graph contains controllers whose PCI parents were never
enumerated at all (`WindowsCategoryMap.cs:18` maps `Usb` to the USB class GUID
only). A walk therefore reports *why* it stopped:

| Termination | Meaning |
| --- | --- |
| `Root` | `ParentId` is null — genuinely parentless |
| `LeftSnapshot` | `ParentId` is set but names no node in this snapshot — the filter excluded it |
| `DanglingParent` | `ParentId` names a node that does not exist on the machine at all (phantom / removed) |
| `Cycle` | the chain re-enters itself — malformed input, not a shallow tree |
| `Unavailable` | this provider does not populate parentage at all (D8) |

**`Ancestors` returns the list *and* the termination**, so "no ancestors" is
never ambiguous. This carries the posture ADR-0068 set for rotation and ADR-0073
D4 restated for layout — *unmeasured is its own state, never a negative
result* — now applied to the edge rather than to the value.

`LeftSnapshot` is **common under a category query** — it is what a controller
whose PCI parent was filtered out reports — though not universal: a filtered
snapshot can also retain a node's parent, or contain a genuine root. The three
near-neighbours are kept apart because a consumer's next
move differs for each: `LeftSnapshot` says *this snapshot cannot see past
here* — **widen the query and you get the answer**; `DanglingParent` says the
parent is not on the machine at all — **widening will never help**;
`Unavailable` says *this provider cannot see parentage at all*. A model that
collapsed any of them into `Root` would be right only on Windows-with-`All` and
wrong on every other query.

**`DanglingParent` is not free, and it is not derivable in the core.** Deciding
that a parent is absent from the *machine* rather than from the *snapshot*
requires asking the OS — one `CM_Locate_DevNode` on Windows — which is IO.

The tempting shape is to hand construction an optional resolver and let it probe
what it cannot resolve. **That is wrong, and it is worth naming because it looks
like the `lookupNode` seam `ResolveLocationPath` already uses.** The difference
is which side of the line the function sits on: `ResolveLocationPath` *is* shell
code, so a resolver is an ordinary dependency there. Graph construction is
*core*, and a core that calls a resolver has its output depend on live machine
state — same input, different answer — which is the one property D3 exists to
guarantee.

So **the shell probes, and passes the answers in as data**: it resolves the set
of unresolved parent ids once, and hands construction a `ParentPresence` map.
Construction stays a deterministic transform of its inputs.

- **With** presence evidence, an unresolved parent is classified `LeftSnapshot`
  or `DanglingParent`.
- **Without** it, the parent stays `LeftSnapshot` — never silently promoted.

The library already makes precisely this call and already names the case:
`LookupNodeForLocation` returns null for *"a phantom/removed parent"*, with
`ResolveLocationPath_MissingParentNode_FallsBackToInstanceId` covering it. The
distinction is worth surfacing because the consumer's next move differs — widen
the query, versus stop looking — but it is a *shell-assisted* refinement of
`LeftSnapshot`, not a state the core can reach on its own.

Consequently:

- **`Root` means the highest ancestor present in the snapshot**, and the API
  says so in that language. It is not a claim about the machine.
- **`Depth` is snapshot-relative** and is meaningful for comparison within one
  snapshot, not as an absolute.
- **Eligible roots** are exactly the nodes terminating `Root` or `LeftSnapshot`.
- **Build the graph from an unfiltered enumeration.** A graph built from a
  filtered list is *honest* — every truncated chain says `LeftSnapshot` — but
  uninformative: on a `DeviceCategory.Usb` snapshot every hub count is
  `Inconclusive`, forever, because the walk can never reach a root hub it was
  never given. Filter the **results**, not the input. This is guidance the API
  should make hard to get wrong, because the failure is silent and the
  motivating consumer would hit it first.

### D5. Cycles are classified at construction, and construction is total

**`Cycle` is a named state, not a bound being hit, and the distinction is not
cosmetic.** At-most-one-parent does not imply acyclic: a chain in which A's
parent is B and B's parent is A satisfies D1's invariant and is still not a
forest. The reflex is to let a depth bound catch it — but then malformed
topology is **indistinguishable from a legitimately deep chain**, which is this
ADR's own failure mode reintroduced one level down. `ResolveLocationPath` has
exactly that gap today: its cyclic-chain test asserts only that the walk is
*bounded*, never that the cycle is *named*.

Cycles are therefore detected **at construction**, not per walk. The graph is an
immutable value built once (D3), so a single O(N) pass classifies every node's
termination up front; a cycle is a structural property of the input and is
found there rather than rediscovered by each caller who walks into it.
**The classification propagates.** A node is `Cycle` if its parent chain *reaches*
a cycle, not only if it lies on one — a node hanging off a cycle has no
terminating acyclic ancestry either, and leaving it looking like an ordinary
node would hand a consumer a walk that never reaches a root with no signal that
anything is wrong. Construction marks the cycle and everything that feeds into
it in the same pass.

Naming it at construction is also what removes the need for a depth bound at
all — D6.

**Detecting a cycle marks it; it does not reject the graph.** Construction is
**total: it never throws, and there is no failure mode to represent.** A
malformed pair of devnodes is one defect in a snapshot of an entire machine, and
throwing would discard the correct topology of every healthy device on the box
because two phantom nodes reference each other — replacing a *named* local
defect with a *total* loss of the answer. The cycle and everything feeding into
it terminate `Cycle`; **every node whose parent chain does not reach a cycle is
unaffected**, and its walk is exactly as conclusive as it would otherwise have
been.

This is ADR-0073 D1 again: Periphery reports what it observed. A cycle **is** an
observation, and `Cycle` is how it is reported. That is also what keeps the
termination reachable — a contract that both promises `Cycle` and rejects every
input that could produce it would be describing a state no consumer could ever
receive or test against.

### D6. There is no depth bound

`Truncated` is therefore not a termination. The obvious move is to carry one — raising `ResolveLocationPath`'s 8, which is
genuinely too low for a general walk (a Logitech HID collection node measured
**12 hops** from termination) to something like 32, against a hand-derived
"legal worst case around 18." Both the number and the mechanism are wrong:

- **The mechanism is redundant.** Once construction has classified every node
  (above), the graph is a finite immutable map and every chain is either acyclic
  — hence at most `N-1` edges — or already marked `Cycle`. A walk over it
  provably terminates with no bound at all. The "never walk unbounded over
  provider-supplied ids" instinct is correct for the *four shipping walks*,
  where every hop is a live cfgmgr or sysfs lookup on an id the library has not
  validated. It does not transfer to a pure walk over ids that were resolved
  once, at construction.
- **A caller-overridable bound contradicts two decisions at once.** Termination
  classified once at construction cannot have been classified against a bound
  the caller had not yet supplied; and `Root(x)` and `Depth(x)` would become
  functions of `(x, bound)`, so two callers would get different roots for the
  same node in the same immutable snapshot — reintroducing exactly the
  multi-valuedness D1 spends its length arguing against.
- **32 was not clear of legal hardware anyway.** The derivation omitted PCIe
  switch depth: each switch contributes an upstream- and a downstream-port
  devnode, and a Thunderbolt daisy chain adds roughly a dozen bridge edges
  beneath a tunneled xHCI. A USB4 dock with stacked internal hubs plausibly
  reaches the high twenties — within a few edges of the bound, where the failure
  is a silent `Truncated` on legitimate hardware. That is the defect this ADR
  indicts `maxDepth: 8` for, reproduced an order of magnitude up.

Dropping the bound removes a public contract state, a counting convention, an
override parameter and the `Inconclusive` branch it fed. The four remaining
terminations are domain facts; `Truncated` was an artifact of a guard that
construction had already made unnecessary.

### D7. `IsUsbHub` / `IsRootHub` are library predicates — and "is the parent a hub?" is a trap

`UsbClassCode.Hub` (0x09) and the `HubClass` subclass triples exist in
`src/Periphery/UsbClassCode.cs`. What does not exist is the distinction that is
actually load-bearing: **root hub versus external hub.**

The naive check is *"is this device's parent a hub?"*, and it never answers the
question asked. For a **directly-attached** device it says *yes* — the parent is
a hub, the root one — which reads as "there is a hub in the way" when there is
not. For a **composite** device it says *no*: D1's own measured chain has the UPS
interface parented to a `USB Composite Device`, two hubs below the root, so the
check misses both of them. It is wrong in one direction and blind in the other.
The predicate that discriminates is *"how many **external** hubs are on the path
to the root hub?"*

Field evidence from two production kiosks running the identical UPS model
(`VID_0665` / `PID_5161`):

| Kiosk | External hubs between UPS and root hub |
| --- | --- |
| A | 0 |
| B | 2 (cascaded) |

Counting external hubs separates them. "Is the parent a hub?" answers *yes* on
both and separates nothing.

Root-vs-external is **platform knowledge** — Windows spells it
`USB\ROOT_HUB30\…`, Linux exposes root hubs as `usbN` devices, macOS as
`IOUSBRootHubDevice` — and platform knowledge is precisely what a consumer
should not be reimplementing. It belongs behind a predicate in the library.

**A counted zero can still be wrong, and this is the decision's sharpest
limitation.** The count is a fact about the *devnode tree*, which is a lossy
projection of the machine. Where a bus is tunneled or redirected, the projection
hides hubs that are physically in the path:

- **USB4 / Thunderbolt docks** tunnel USB3 over PCIe. A UPS in the dock walks up
  to the dock's *internal* root hub and counts **0 external hubs**, with an
  entire dock and cable in the power path.
- **usbipd-win, VMBus, RDP redirection** parent the device to a synthetic bus
  node; the real hub chain is on another machine or partition. Again `0`.

That is a **confidently wrong** answer, not an unavailable one — the failure D8
exists to prevent, arriving through a door D8 does not watch, and it lands
precisely on this ADR's motivating consumer. Periphery cannot detect it from
parentage alone, so it must not be papered over: the count is documented as *"external hubs
**on the enumerated path**"*, and D9's boundary is what keeps the library from
promising more. A consumer whose correctness depends on the physical power path
needs deployment knowledge, exactly as D9 says.

**Both predicates return `Tri`, not `bool`** — and this ADR owes that
correction to ADR-0079 D7, which worked out why. A bare `bool IsRootHub` puts
"this node is not a root hub" and "I cannot tell what this node is" behind the
same `false`, which is D8's own rule broken by D8's own companion decision: D8
forbids a bare nullable and a bare number, and simply did not think of a bare
bool. On a plane where the class code is unreadable the honest answer is
`Unknown`, and `Tri` is already in the shapes table below for `SameContainer`.

Note that both predicates are **properties of a node**, not of its position, so
they are unaffected by D4's snapshot-relative root. Hub *counting* is
position-dependent and therefore inherits D4's termination: a count over a walk
that ended `Cycle`, `Unavailable`, or `LeftSnapshot` before reaching
a root hub is **not zero** — it is unknown, and the API must not let a
consumer read it as zero. That is the exact failure mode this ADR's motivating
consumer would hit.

### D8. Availability is a state, not a null

"Populate topology off-Windows *or* distinguish unavailability" is a
disjunction, not a decision: it can be satisfied while still returning an empty
ancestor list on Linux. So:

**Each topology relation reports one of three things: a value, a measured
absence, or `Unavailable`.** Never a bare null that a caller can misread.

| Relation | Windows | Linux / macOS today |
| --- | --- | --- |
| parentage | value / `Root` | Linux: **populated from the syspath**. macOS: `Unavailable` — *not* `Root`, *not* empty |
| `PortNumber` | value / measured-absent | `Unavailable` |
| `ContainerId` | value / measured-absent | `Unavailable`, and `SameContainer` → `Unknown` (D2) |

The failure this rules out is concrete: a Linux consumer counting external hubs
gets `Unavailable` and must handle it, instead of getting `0` and concluding the
UPS is directly attached. `0` and "I cannot see the bus" are the same number and
opposite facts.

**The policy is not enforceable without shapes, so the shapes are part of the
decision.** A three-state *policy* can be honoured on paper by an
implementation that still returns `IReadOnlyList<DeviceInfo>` and `int?` — and a
Linux caller then reads an empty list as "no ancestors" exactly as before. The
binding rule:

> **No topology query returns a bare collection, a bare nullable, or a bare
> number.** Every one returns a result that carries its own conclusiveness, and
> the payload is not reachable without passing the state.

"Not reachable" is meant literally, because the objection this rule answers is
that a policy stated in prose can be honoured by a type that still lets a caller
ignore it. The binding form is a **discriminated result whose payload lives on
the conclusive case only** — `TryGet(out …)`-style, or a match over the state —
**not** a struct with a `Status` field beside an always-readable `Value`. There
is no `.Value` to read after an inconclusive result, and therefore no
`.Value`-and-a-shrug: an implementation that exposes one is not an
implementation of this ADR. The exact C# spelling is left to the
implementation; the invariant — *the state cannot be bypassed to reach the
payload* — is not.

| Query | Returns | Not |
| --- | --- | --- |
| ancestors | the ordered list **and** a `Termination` (D4) | `IReadOnlyList<DeviceInfo>` |
| children, descendants | `ChildSet` — the known set, plus `IsComplete` only under `SubtreeClosure` (D3) | `IReadOnlyList<DeviceInfo>` |
| root / depth | a value **and** the terminating reason it derives from | `DeviceInfo` / `int` |
| port, container id | present-value / measured-absent / `Unavailable` | `int?` / `Guid?` |
| external hub count | a count **or** `Unavailable` / `Inconclusive` | `int` |

**Concretely** — ADR-0002 sketched its types, and four review rounds have shown
that prose alone leaves this unenforceable, so:

```csharp
public enum AncestorTermination { Unavailable = 0, Root, LeftSnapshot, DanglingParent, Cycle }

// Payload lives on the conclusive case only: there is no `.Value` to read past
// a state you did not check, and no default that reads as an answer.
public readonly struct AncestorWalk
{
    public AncestorTermination Termination { get; }
    public bool TryGetChain(out ImmutableArray<DeviceInfo> chain);  // false unless Root
    public bool TryGetPartialChain(out ImmutableArray<DeviceInfo> chain,
                                   out AncestorTermination why);    // what was seen before stopping
}

public readonly struct HubCount            // D7
{
    public bool TryGetExternalHubCount(out int count);  // false when the walk was inconclusive
    public AncestorTermination Termination { get; }
}

public readonly struct ChildSet            // D3/D8 — downward completeness
{
    public ImmutableArray<DeviceInfo> Known { get; }    // always safe: what IS here
    public bool IsComplete { get; }                     // true only under SubtreeClosure
}

public enum Tri { Unknown = 0, No, Yes }   // SameContainer (D2); Unknown at ordinal 0
                                           // Defined by ADR-0079, which ships first
```

Three properties are load-bearing and an implementation that drops any of them
is not an implementation of this ADR: **`Unavailable` and `Unknown` sit at
ordinal 0**, so a default-constructed value asserts the least (ADR-0073 D4);
**there is no readable payload beside a status field**; and **`ChildSet.Known`
is deliberately always readable** — the set of devices that *are* present is a
fact regardless of completeness, and forcing a `TryGet` there would push callers
toward ignoring `IsComplete` rather than reading it.

The graph value itself also carries a single `ParentageAvailability`, so a
consumer checks once at construction rather than pattern-matching every node —
`Unavailable` is a property of the provider, not of the device, and forcing it
to be rediscovered per-node would be the kind of ceremony that gets suppressed
with a `.Value` and a shrug.

The hub count is the row that matters most: it is the one the motivating
consumer reads, and `int` has no way to say "I could not see."

**The downward direction needs the same armour, and is easier to forget.**
`Children` and `Descendants` are built by inverting `ParentId` across the
snapshot, so the same cut truncates them — but an incomplete child set has *no
observable signal at all*, where a truncated ancestor chain at least ends
somewhere nameable. Concretely: on a `DeviceCategory.Hid` snapshot a hub's real
children are USB composite nodes, which the filter excluded, so `Descendants`
returns **empty** and the consumer reads "nothing is plugged into this hub."
That is D8's own failure arriving through the door D8 nearly left unwatched.
`IsComplete` is therefore false for that snapshot — a *completed* category query
is not a *closed* one, and only `SubtreeClosure` (D3) licenses the claim.

**`ContainerId` is not synthesized off-Windows.** Neither libudev nor IOKit has
an equivalent, and approximating an equivalence relation by grouping interfaces
under a shared parent would produce groups that are *wrong* rather than absent —
which is what D2 and ADR-0073 both argue against. It reports `Unavailable` until
a platform supplies a real one.

**The provider work for parentage is mechanical, but slightly less far along
than it looks.** `udev_device_get_parent` is already declared at
`src/Periphery/Linux/UdevInterop.cs:70` — and is never called anywhere. On
macOS, `IOKitInterop` binds `IORegistryEntryGetNameInPlane`,
`IORegistryEntryCreateCFProperties` and friends, but **does not bind
`IORegistryEntryGetParentEntry` at all**. So it is one unused P/Invoke on Linux
and one new P/Invoke on macOS, against libraries both providers already load and
call. No research; some code.

**But `Unavailable` is the wrong answer for Linux parentage, and this ADR does
not claim it.** Periphery *already holds* Linux ancestry: `Id` and
`LocationPath` are both the sysfs syspath (`LinuxDeviceProvider.cs:254, 264`),
and a node's parent is its parent directory. Two shipping backends already
ascend it (`LinuxHidBackend.ResolveDevNode`, `LibUsbBackend.ResolveUsbfsNode`).
Reporting `Unavailable` over data in hand would mean *"observed, and declined to
parse"* — which is not a state ADR-0073 D1 endorses, and this ADR invokes
ADR-0073 too often to then ignore it on the one platform where the observation
is already made.

So **Linux parentage is derived from the syspath**, not from
`udev_device_get_parent` — the string is already there and costs no P/Invoke,
while the declared-and-unused binding costs a call per node for the same answer.
Measured, it is cheaper still than this ADR first claimed: sysfs nests **one
directory per hop** (`…/usb9/9-3/9-3.1/9-3.1.1`), so a node's parent is its
parent *directory* and deriving it is `dirname`, not string surgery. Interface
nodes sit as children of the device (`9-3.1.1:1.0`) and need no special case.
ADR-0079 D2 carries the measured grammar.
`Unavailable` is reserved for **macOS**, where nothing has been read, until the
`IORegistryEntryGetParentEntry` binding lands. That the syspath is
*string-shaped* is the objection D11 withdraws.

### D9. Periphery answers *what the topology is*, never *what it means*

The motivating consumer wants to know whether a UPS can still report its state
during a mains outage. Periphery must not answer that.

Periphery answers: this devnode's ancestors are these, the walk terminated for
this reason, two of them are external hubs, these three devnodes share a
container. Whether that makes a UPS's mains reporting trustworthy depends on
which hubs are bus-powered, how the kiosk is wired, and what the deployment
guarantees — **the kiosk consumer domain knowledge, which Periphery does not have and
cannot acquire.**

This is ADR-0073 D1 restated for a second subsystem, and it is stated here
explicitly because this is exactly the boundary that erodes quietly: the
topology API will make the domain inference look like it is one small helper
away, and the helper would encode a deployment assumption as a library fact.

### D10. Two shapes, not one: a shell-side walker over live nodes, and this graph over a snapshot

> **Split out and kept — the one exception to the historical marker above.** This
> decision is now [ADR-0080](0080-ancestor-walking-is-one-fold.md) and is *not*
> rejected with the rest of this document. It is retained here because the argument
> below is what established that the walker and the graph are different shapes, and
> rejecting the graph does not touch it. Read the *comparison* below as live and the
> "Snapshot graph (D3)" column as historical.

The four shipping walks (Context) all resolve against **live OS handles** and
deliberately traverse outside any snapshot. `ResolveLocationPath` runs *inside*
`ToDeviceInfo`, while the list this graph is built from does not exist until
enumeration finishes — so **the walk that motivated this ADR cannot call the
type this ADR proposes.** Reading that prior art as demand for a pure snapshot
graph was reading it backwards.

Both shapes are real, and they are not the same thing:

| | Shell walker | Snapshot graph (D3) |
| --- | --- | --- |
| Input | one id, resolved live | a device list + provenance |
| Consumers | the four in-library walks | callers who already hold a list |
| IO | every hop | none |
| Answers | ancestry of *one* node | ancestry, descendants, roots, grouping |

What they share is the part worth sharing: the **pure classification core** —
the termination taxonomy (D4), cycle rules (D5), and the no-bound argument
(D6) — expressed as a fold over `(id → parent)`, with the walker supplying hops
from cfgmgr/sysfs and the graph supplying them from its map. That is exactly the
`lookupNode` seam `ResolveLocationPath` already has, generalized.

**Consolidating the four walks is therefore the walker's job, not the graph's**,
and this ADR withdraws the claim that the graph does it. Three bounds across
four implementations remains the strongest evidence for the shared core; it is
simply evidence for a different shape than the first draft assumed.

### D11. The `PortPath` parser (ADR-0079) ships first, and narrows this ADR

Two of the three consumers in the Context — hub counting and board-to-board
correlation — do not need a graph. `LocationPath` already encodes the answer,
and ADR-0079 parses it: external hubs are a hop count, and the hub / root-port
/ controller questions are comparisons over the parsed hop vector.

The objection this ADR previously raised — that it is *"a string-shaped truth
about a structural fact, which has gone badly in this repo before"* — does not
survive contact with the evidence. It is aesthetic, three string-shaped
structural walks already run in production here, and the parser is *validated
against the very ancestry it stands in for*: 42 `USB\VID_*` devices, 42
agreements with an independent `DEVPKEY_Device_Parent` walk, 0 disagreements.

**And the limit of that validation is a limit this ADR shares.** Both sides of
ADR-0079's comparison read the same cfgmgr32 devnode tree, so the agreement
shows its parser is faithful to that tree — not that the tree is faithful to the
machine. That is not a point in the graph's favour, because **the graph is built
from the same snapshot of the same tree**. Neither approach can see a hub that
enumeration hides (D7), and neither is more exposed to it than the other. The
string-versus-value question is therefore settled on parser correctness, where
ADR-0079 has a measurement and this ADR has none.

**ADR-0079's validation covers the population this ADR cares about least, and
its D4 says so.** The 42 are devices whose `LocationPath` the OS supplied
directly. The `HID\*` function nodes — whose path
`WindowsDeviceProvider.ResolveLocationPath` synthesizes, and where both
motivating consumers actually live — are not in it. So the withdrawal above is
of the *aesthetic* objection only; the evidentiary question stays open, and it
is ADR-0079's blocking item rather than this ADR's.

So ADR-0079 lands first — Windows-only when this was written, though **ADR-0079
D2 now has Linux measured and in scope**, and `PortPath` ships with both
grammars — and this ADR is **narrowed to what a parser cannot do**: arbitrary
ancestry over non-USB planes, descendants, container grouping, cycle and
completeness reporting, and roots. Those are real, and none
of them has a consumer yet — which is the honest statement of this ADR's
position, and is why it stays **Proposed** rather than being accepted alongside
0079.

The narrowing above was recorded as contingent on an ADR-0079 that was itself
Proposed. **That contingency has resolved: ADR-0079 is Accepted**, its D4 re-run
discharged over five runs and four topology changes with zero disagreements, and
its external-hub count validated against an independent devnode walk at 0, 1, 2
and 3 hubs. So the reduction of scope here is settled rather than provisional,
and the two consumers it claims do not revert to this ADR.

The same-tree limitation noted above also has an answer now, and it is not one
this ADR can match: on Linux the count is cross-validated against a **declared**
QEMU topology (ADR-0079 D4), which is ground truth from outside any enumeration.
A graph built from a snapshot cannot be checked that way without the same
fixture, so on the evidence available the parser is the better-validated of the
two.

## Consequences

- **ADR-0002 D3 is reversed, and the reason is evidence, not preference.** The
  trigger is not that a tree-builder became more convenient; it is that the
  library wrote it privately four times over, and the "trivially
  computable" claim in ADR-0002 D2 was falsified by the depth bound, lookup seam,
  missing-node fallback and seven tests that the private version needed.
- **The four shipping walks are consolidated by the walker (D10), not by this
  graph.** They share only the pure classification core; `ResolveLocationPath`
  runs *during* enumeration over live cfgmgr lookups and cannot call a type
  built from the finished list. Its `maxDepth: 8` stays — correct for a walk
  that stops at the first ancestor with a port. Its seven tests seed the shared
  core's suite.
- **The single-parent invariant costs nothing to hold: it is unrepresentable to
  violate.** `DeviceInfo.ParentId` is one nullable field, so a second parent
  cannot be expressed by the input at all — D1's invariant is enforced by the
  data shape, not by a runtime assertion. **Acyclicity is the one that needs
  real work**, and D5 puts it in the construction pass as a classification
  rather than a rejection — so **construction is total and never throws**: any
  device list yields a graph, and defects are named on the nodes they affect.
  The cost is that a consumer cannot infer a well-formed forest from the fact
  that construction succeeded. They read the termination.
- **Every query that can be inconclusive says so in its return type**, which is
  the main cost of this design: `Ancestors` returns a termination alongside the
  list, `SameContainer` is three-valued, and hub counts are optional. That is
  more ceremony than a plain list, and it is the ceremony that stops a consumer
  reading "unavailable" as "zero."
- **Linux and macOS gain three fields, not one.** `PortNumber` and `ContainerId`
  are as Windows-only as `ParentId`; an ancestors API that works cross-platform
  while `PortNumber` stays null yields a tree that can be walked but not
  rendered.
- **The graph itself costs no new IO** — it is a value transform over a list the
  caller already has. This is *not* a claim that the enumeration path is
  IO-free: `ResolveLocationPath` already makes per-node cfgmgr calls from inside
  `ToDeviceInfo`, and adding `DanglingParent` (D4) adds one `CM_Locate_DevNode`
  per unresolved parent. Those costs are on the shell, and they predate or
  accompany this design rather than being avoided by it.

## Open questions

Genuinely open — each needs evidence this ADR does not have. Several questions
that were here are now decided above, because leaving them open left decisions
resting on them: root identity (D3, D4), the depth bound (D6), off-Windows
`ContainerId` (D8), the core-versus-shell shape (D10), and whether a parser
serves the demonstrated consumers (D11, ADR-0079).

- **Does `LeftSnapshot` mean the same thing on Linux?** ***Answered: yes.***
  It is defined here against SetupAPI's class-GUID cut, and whether libudev's
  subsystem filtering produces a comparable cut was this ADR's blocking item.
  Measured on the Linux device rig, against a QEMU nested-hub fixture built for
  the purpose (root hub → hub → hub → device, since the rig otherwise has no
  tree with any interior):

  ```
  9-3.1.1  →  9-3.1  →  9-3  →  usb9       SUBSYSTEM=usb
                                    ↓
                            0000:00:01.0   SUBSYSTEM=pci
  ```

  **The semantics here are `SUBSYSTEM` (exact, the device's own), not
  `SUBSYSTEMS` (the device *or any ancestor*)** — the distinction matters and an
  earlier draft of this passage got it wrong. Under `SUBSYSTEMS` every USB node
  beneath a PCI controller also matches `pci`, and no cut would be visible at
  all. Exact match is also the *right* semantics rather than merely the one used:
  it is what libudev's `udev_enumerate_add_match_subsystem` performs, which is
  how a filtered enumeration is actually built.

  The evidence is **per-device and local to the fixture**, not a global count.
  Every node a `SUBSYSTEM=usb` enumeration yields for bus 9, with its parent and
  whether that parent is also in the set:

  | node | parent | parent's own subsystem | in the set? |
  | --- | --- | --- | --- |
  | `usb9` (root hub) | `0000:00:01.0` | **`pci`** | **no → `LeftSnapshot`** |
  | `9-1`, `9-2`, `9-3` | `usb9` | `usb` | yes |
  | `9-3.1` | `9-3` | `usb` | yes |
  | `9-3.1.1` | `9-3.1` | `usb` | yes |
  | `9-0:1.0`, `9-1:1.0`, `9-2:1.0`, `9-3:1.0`, `9-3.1:1.0`, `9-3.1.1:1.0` | their device | `usb` | yes |

  Twelve nodes; **eleven have their parent in the set, and exactly one exits** —
  the root hub, at the PCI boundary. `9-3 → 9-3.1 → 9-3.1.1` is the hub chain the
  QEMU arguments declared, so the tree under test is the tree that was asked for.

  That single exit point is the whole claim: the USB plane is **closed under
  parentage up to the root hub**, and the chain leaves the snapshot at exactly
  the place it leaves a `DeviceCategory.Usb` snapshot on Windows. The *mechanism*
  differs (exact subsystem match versus class GUID); the *shape* is identical,
  which is what D4's contract needs.

  (Machine-wide there are 39 `SUBSYSTEM=usb` and 28 `SUBSYSTEM=pci` records, and
  the sets are disjoint because a device has exactly one `SUBSYSTEM`. That is
  context, **not** the argument — global totals say nothing about which nodes are
  descendants of which, and an earlier draft leaned on them as though they did.)

  **macOS remains unmeasured.** IOKit plane traversal is a different model again
  and no macOS host was available; that is now the only part of this question
  still open, and it is narrower than the original blocking item.

  Reproducing commands and the QEMU wiring are in
  [`docs/explorations/device-emulation-and-graph-walking-2026-08.md`](../explorations/device-emulation-and-graph-walking-2026-08.md).

- **Does keeping system-plane nodes survive a real consumer?** *Decided, with a
  revisit trigger.* With `DeviceCategory.All` on Windows a mouse's ancestors
  include `ACPI\PNP0A08\0` and `ROOT\ACPI_HAL\0000` — real devnodes that no
  consumer of this API is asking about. They stay, because filtering them would
  be Periphery deciding which devices are interesting (ADR-0073 D1). Note this
  is a *different* judgement from D3's exclusion of `HTREE\ROOT\0`, and the line
  between them is the whole question: the synthetic root is excluded because it
  **names no device at all**, not because it is uninteresting. If that line ever
  has to be drawn on "interesting" rather than "is a device", it has moved to
  the wrong place. The first consumer that renders a tree tests it.
- **~~Does a consumer want the graph at all?~~ Answered: no, and that is why
  this ADR is Rejected.** D11 narrowed it to the questions a parser cannot
  answer, and none of them acquired a consumer. ADR-0079 shipped, nobody asked,
  and the reject trigger stated here fired on 2026-08-24. The accept trigger is
  unchanged and is now a **reopen** trigger: a consumer asking for descendants
  across a plane `PortPath` does not parse, for container grouping as a tree
  rather than a `ContainerId` group-by, or for ancestry on Linux or macOS. That
  is a new ADR, not an edit to this one. See the Status section.

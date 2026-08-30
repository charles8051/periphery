---
title: "ADR-0080: Ancestor walking is one shell fold with four targets, not four walks with three bounds"
status: "Proposed"
status_note: "Not implemented on `main`. Topology code lands on the `topology` branch; the ADR and its evidence go to `main` first."
date: "2026-08-24"
authors: "@charles8051"
tags: ["architecture", "decision", "topology", "device-tree", "usb", "windows", "linux", "sysfs", "cfgmgr", "functional-core", "refactor"]
supersedes: ""
superseded_by: ""
split_from: "0078-device-topology-is-a-rooted-forest.md"
---

# ADR-0080: Ancestor walking is one shell fold with four targets

## Status

Split from **ADR-0078 D10**, which is the only part of that ADR not rejected
with it. ADR-0078 argued for a pure snapshot graph and was rejected on 2026-08-24
because no consumer asked for one. D10 is a separate finding about code that
already ships, and it survives the rejection intact.

Carries ADR-0073 D1 — *Periphery reports what it observed; the consumer forms
the verdict* — and ADR-0073 D4's posture that an unmeasured state is its own
state, never a negative result.

## Context

The library ships **four independently written ancestor walks**, verified in
`src/` at the time of writing:

| Walk | Bound | Traverses | Stops at |
| --- | --- | --- | --- |
| `WindowsDeviceProvider.ResolveLocationPath` (`WindowsDeviceProvider.cs:241`, `maxDepth` `:246`) | **8** | live cfgmgr nodes via `LookupNodeForLocation` | first ancestor carrying a `LocationPath` |
| `WindowsDeviceReset.TryResolveUsbAncestor` (`WindowsDeviceReset.cs:217`, `MaxAncestorWalk` `:61`) | **16** | live cfgmgr via `CM_Get_Parent` | first ancestor whose id is a USB devnode |
| `LinuxHidBackend.ResolveDevNode` (`LinuxHidBackend.cs:403`, loop `:423`) | **8** | sysfs directory ascent | first ancestor with a `hidraw/` child |
| `LibUsbBackend.ResolveDevNode` (`LibUsbBackend.cs:770`, loop `:790`) | **8** | sysfs directory ascent | first ancestor with `busnum` and `devnum` |

Four implementations, **three different bounds**, no shared tests. Each was
written for its own call site and none knew the others existed — the repo had
already picked 16 for a structurally identical traversal by the time the third
and fourth picked 8.

`ResolveLocationPath` is the one that shows the shape. It is `internal static`,
it takes a `lookupNode` seam so the IO stays in the shell, it has a
missing-parent fallback, and it carries seven unit tests against a fake chain
with no hardware (`tests/Periphery.Tests/Platform/WindowsDeviceProviderTests.cs`).
Its own remarks call the walk *"a pure fold over the chain so it is unit-testable
without hardware."* That is the right description, and it is the description of
all four.

### The defect is not that the bounds differ

**It is tempting to read "three bounds" as "two of them are wrong", and that
reading is what ADR-0078's first draft got wrong.** Each walk stops at a
different target, so each has a different legitimate depth. A walk that stops at
the first ancestor carrying a port is genuinely shallower than one that ascends
to a USB devnode, and ADR-0079's measurement bears that out: on the Windows workstation, over
five probe runs, `ResolveLocationPath`'s **deepest real walk was 2** against its
bound of 8 (`portpath-parse-vs-devnode-walk-2026-08.md`). The exhaustion case
cannot be produced by plugging hardware in.

The defect is that four call sites independently re-derived a fold, a bound, a
seam, a missing-node fallback and a termination convention, and that **only one
of the four has tests**. Three of them have never been exercised against a
cyclic chain, a missing ancestor, or an exhausted bound in any test at all.

### What a bound actually hides

`ResolveLocationPath`'s bound exists so *"a broken/cyclic chain can never loop"*
(`WindowsDeviceProvider.cs:240`). Its cyclic-chain test asserts only that the
walk **is bounded** — never that the cycle is **named**. This is ADR-0078 D5's
finding, and it applies here rather than to the rejected graph: a bound that
catches a cycle makes malformed topology indistinguishable from a legitimately
deep chain, and the caller gets the same answer for both.

The other three do not even have that. `LinuxHidBackend.ResolveDevNode` walking
off the top of a sysfs chain and the same walk exhausting its bound on a cycle
produce the identical outcome, and neither is distinguishable from *"this device
has no hidraw node"*.

## Decision

### D1. One fold in the shell, parameterized by a lookup and a target

The shared piece is a **fold over `(id → parent)`**, with each call site
supplying two functions and reading a structured result:

- **`lookup`** — one hop, supplied per call site. This is IO and it stays in the
  shell, which is where all four walks already are. It is
  `ResolveLocationPath`'s existing `lookupNode` seam, generalized.
- **`target`** — the predicate that ends the walk successfully. "Carries a
  `LocationPath`", "is a USB devnode", "has a `hidraw/` child", "has `busnum`
  and `devnum`".

**`lookup` is a *per-call-site* hop, not a claim that the platforms have one
parent relation.** `CM_Get_Parent` resolves the devnode parent. `dirname` on a
syspath resolves the *directory* above, and those are not the same operation:
sysfs interposes non-device container directories, so `dirname` of
`…/target0:0:0/0:0:0:0/block/sda` is `…/block`, which is not a device at all.
The two shipping sysfs walks get away with directory ascent because their
targets are narrow — a `hidraw/` child, a `busnum`+`devnum` pair — and their
inputs are USB and HID syspaths where no container sits between a node and its
parent.

So the fold is generic over `lookup`; **`dirname` is not promoted to a general
Linux parent lookup, and this ADR does not define one.** A caller that needs the
real udev device parent needs `/sys/…/device` symlink resolution or a libudev
call, which is a different function with a different failure mode. Nothing here
requires it, and adding it speculatively is what ADR-0078 was rejected for. If a
fifth call site ever wants ancestry over an arbitrary syspath, that is the point
to define it and to document which syspath forms are supported.

The fold itself is pure over those two functions: same sequence of lookup
answers, same result. That is what makes it testable against a fake chain with
no hardware, which is the property `ResolveLocationPath`'s seven tests already
exploit and the other three walks cannot.

**This is a shell utility, not a core value type.** ADR-0078 D10's finding is
the reason: every one of these walks resolves against **live OS handles** and
deliberately traverses nodes no snapshot contains. `ResolveLocationPath` runs
*inside* `ToDeviceInfo`, before the enumerated list exists. A walk that cannot
run until enumeration finishes is a different thing, and ADR-0078 was rejected,
so there is no snapshot type for this to share a core with.

### D2. The walk reports why it stopped

A bare `string?` return conflates *"no ancestor matched"* with *"the chain was
malformed"* with *"the bound ran out"*. All four walks do this today.

The termination taxonomy is **ADR-0078 D4's, minus the states that were about
snapshots**:

| Termination | Meaning |
| --- | --- |
| `Found` | an ancestor satisfied `target`; the walk carries it |
| `NoParent` | the chain reached a node with no parent without matching |
| `Missing` | a parent id was named but `lookup` could not resolve it — phantom or removed |
| `Cycle` | the chain re-entered itself |
| `Exhausted` | the bound ran out with the chain still ascending |

`LeftSnapshot` and `Unavailable` do not appear. Both are properties of a
filtered device list, and this walk has no list — it queries the OS directly, so
there is no cut for a chain to leave.

**`Cycle` and `Exhausted` are separate states, and separating them is most of
the value here.** Today both surface as the same fallback. A cycle is malformed
input and widening the bound will never help; an exhausted bound means the
machine is deeper than anyone expected and the bound is the thing to look at.
Collapsing them means a real cycle gets diagnosed as "bump the constant" and a
legitimately deep chain gets diagnosed as "the hardware is broken."

Detecting the cycle costs the shell a visited set of ids, which it can afford:
these walks are bounded at 8 and 16 and the deepest measured real walk is 2.

### D3. Each call site keeps its own bound, and the bound stops being a cycle guard

**No unified constant.** Each walk retains the bound its target justifies, and
`ResolveLocationPath` keeps `maxDepth: 8` — ADR-0078's Consequences already
concede this, and ADR-0079's measurement supports it at a deepest real walk of 2.
A single shared number would have to be the maximum of four unrelated targets'
worst cases, which makes it wrong for three of them.

What changes is **what the bound is for**. With `Cycle` detected explicitly
(D2), the bound is no longer *"so a broken chain can never loop"* — it is a
budget on how far a healthy chain is worth ascending for this particular target.
That is a statement each call site can defend, where the current one is a
statement none of them measured.

**The bounds on the three walks that are not `ResolveLocationPath` are
unmeasured**, and this ADR does not pretend otherwise. See the open questions.

### D4. The seven existing tests seed the suite; the other three walks get covered

`WindowsDeviceProviderTests` already covers the fold's interesting cases against
a fake chain: the cyclic one, the missing-parent one, the immediate-match one,
the exhaustion one. Those move to the shared fold, and the three walks that have
never had a cyclic-chain or missing-ancestor test acquire one by construction.

That is the concrete deliverable and the reason this is worth doing at all.
Three shipped code paths currently have no test for the failure modes the fourth
one found worth writing seven tests about.

### D5. This is a refactor, not a behaviour change, with one exception

Every call site returns what it returns today for every input it handles today.
The one deliberate change is that a caller can now **distinguish** the failure
modes it previously could not, which is additive: the existing fallbacks stay,
and the termination is available alongside them.

`ResolveLocationPath`'s instance-id fallback stays exactly as it is. ADR-0079 D7
depends on it — 56 instance-id fallbacks were measured, of which 0 parsed as a
port path — and that behaviour is now load-bearing for a shipped type.

## Consequences

- **Three shipped walks gain the tests the fourth already has.** This is the
  whole return. Nothing new is enumerated, no new IO is added, and no public
  surface changes.
- **The fold is `internal`.** It serves four in-library call sites and has no
  external consumer, which is the same standard ADR-0078 was rejected against.
  If it is ever public, that is a separate decision with a consumer behind it.
- **A cycle becomes diagnosable.** Today a cyclic devnode chain and a chain 9
  levels deep are the same log line. After this they are different terminations.
- **ADR-0078's D4, D5 and D6 are cited, not restated.** The termination
  taxonomy, the cycle-classification argument, and the case against treating a
  bound as a cycle guard are all developed at length there and remain readable
  in a Rejected document. This ADR takes the parts that apply to a live walk and
  explicitly drops the parts that were about snapshots.
- **This does not resurrect the graph.** Nothing here answers descendants, roots,
  container grouping, or ancestry across a plane the caller has no handle into.
  Those were ADR-0078's, and they stay rejected.

## Open questions

- **Are 8 and 16 the right bounds for the three unmeasured walks?** Only
  `ResolveLocationPath` has a measurement behind it (deepest real walk 2,
  ADR-0079 D4). `TryResolveUsbAncestor`'s 16, and the 8 on both sysfs ascents,
  were chosen without one. With `Exhausted` as its own termination (D2) the
  answer becomes observable in the field rather than needing a rig, which is the
  cheapest way to close this: ship the taxonomy, then look at whether
  `Exhausted` ever appears.
- **Does the sysfs ascent want the same fold as the cfgmgr walk?** Two things
  differ, and D1 keeps both out of the shared abstraction rather than papering
  over them. `dirname` is a string operation with no failure mode, where
  `CM_Get_Parent` is a call that can fail, so `Missing` is reachable on Windows
  and arguably unreachable on Linux. And `dirname` ascends *directories*, not
  the device tree — the container-directory case in D1 is the concrete way those
  diverge. One fold with a state one platform never produces is still better
  than two folds, but this has not been written yet and the shape may argue
  otherwise. If it does, the answer is two folds, not a `dirname` promoted to a
  parent lookup it is not.
- **Should `V4l2CameraBackend.ResolveDevNode` and
  `I2cDdcMonitorBackend.ResolveDevNode` join?** Both share the name and the
  purpose but neither ascends — they resolve directly. They are out of scope
  here, and the risk is that a future edit turns one into a walk and
  re-derives the fold a fifth time.
- **What is the trigger to accept this?** It is a refactor of shipped code with
  a stated deliverable, so unlike ADR-0078 it needs no consumer. It needs a
  decision that three untested walks are worth the churn.

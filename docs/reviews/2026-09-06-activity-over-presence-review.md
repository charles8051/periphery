# Independent review of the 2026-09-06 merges to `main`

**Date:** 2026-09-06
**Status:** Actioned. One code fix and the documentation corrections are in PR #210. The
remaining findings are issues #202 through #209 and a comment on #201. Everything else
below is recorded as checked and not filed.
**Method:** Five reviewers, one per change cluster, each in its own git worktree at
`bfcbd4e`. Each read the whole of the files it owned rather than the diff, ran the
relevant test project, and checked test quality by reverting production hunks and
confirming the new tests fail. Where a suspicion could be settled by a throwaway test
against the fake providers, it was written, run, and deleted. A full solution build and
test run at `bfcbd4e` set the baseline.
**Scope:** the nine commits between `f1002b2` and `bfcbd4e`, all merged the same day and
all on one theme: activity is the edge to act on, presence is inventory (ADR-0087,
ADR-0088).

| Commit | PR | Change |
|---|---|---|
| `1c5b8a6` | #190 | docs: investigation trim |
| `6903106` | #192 | docs: issue references repointed after a refile |
| `ebaa05a` | #194 | FlashAnything: the bootloader wait is a readiness gate |
| `d616ef8` | #196 | Treehopper control: read the board version on `Activated` |
| `efa1ece` | #197 | Treehopper control: hotplug test seam, close the session on `Deactivated` |
| `351139b` | #195 | ADR-0088, plus XML remarks on `DeviceWatcher.Appeared` / `Activated` |
| `8e129fb` | #188 | ADR-0087 |
| `0b93f0f` | #193 | Watcher: reconcile activity at the fan-out boundary |
| `bfcbd4e` | #186 | Windows: raise `Appeared` from the devnode stream |

## How to read this

CONFIRMED means a probe, a reverted hunk, or a real device demonstrated the finding.
PLAUSIBLE means it follows from reading and was not demonstrated. Severity is High,
Medium or Low. Line numbers are at `bfcbd4e`.

## Baseline

| | |
|---|---|
| Build | `dotnet build Periphery.slnx -c Release`: 0 errors |
| Tests | 24 projects, 3009 tests: 2997 passed, 12 skipped, 0 failed |

---

## 1. Watcher core: #193, #195, and the watcher part of #186

Files: `src/Periphery/DeviceWatcher.cs`, `tests/Periphery.Tests/Tracker/`.

### Findings

**1.1 High. Reconciliation is stale by the time of tracker fan-out. CONFIRMED, pre-existing.**
`ReconcileActivity` and the D2 skip check run at the top of `OnProviderAppeared`
(line 1258); the public `Appeared` raise then runs consumer code synchronously; the
tracker fan-out afterwards delivers a payload reconciled against `_knownConnectedIds` as it
was at the top. If a live `Activated` lands in that window, the tracker goes `Active`, the
fan-out then demotes it to `Present`, and `OnProviderActivated`'s dedup guard (line 1300)
blocks re-activation for good. The mirror (a `Disappeared` in the window) latches the
walk's `Active` payload for a device that is gone. Three probes confirmed the live path, the
walk path, and the mirror. All three also fail at `f1002b2`, so this is the #177 class left
open rather than a regression. Recorded on #201, with a narrowing that does not need
recency: re-run `ReconcileActivity` immediately before fan-out, and adopt ADR-0087's
Option 2 so `ApplyAppeared` cannot lower a held connected latch.

**1.2 Medium. `KnownDevices` returned the whole device tree once a tracker was registered.
CONFIRMED, regression.** The replay cache is written before the watcher filter check on
both the walk (line 1093) and the live path (line 1264), because it feeds `Reconfigure`
and a tracker may hold devices the watcher-level filter rejects (ADR-0087 D3). The getter
(line 683) copied that cache unfiltered. With a tracker registered the walk enumerates
unfiltered, so the property returned everything, against its own doc comment. A probe
passes at `f1002b2` and fails at `bfcbd4e`; the existing `KnownDevices_RespectsWatcherFilter`
misses it because it registers no tracker. Fixed in #210: the getter applies the filter
on read, outside the cache lock.

**1.3 Low. ADR-0087 disagreed with the code it describes.** Status said "Proposed / Not
implemented" after #193 shipped it; D2 said the skip set is "populated by the four
provider handlers" when the code deliberately uses the two presence handlers (lines 1256
and 1347); the consequences section said a shippable D2 "must scope" the set when the
shipped one does (`OpenSnapshotWindow` / `CloseSnapshotWindow`, lines 157 to 173). Fixed
in #210.

**1.4 Low. A late `Deactivated` after `Disappeared` resurrects the device in the replay
cache. PLAUSIBLE.** Line 1326 writes on `Deactivated`; line 1351 removes on
`Disappeared`. Under the concurrency the comment at line 1354 handles, a late
`Deactivated` re-adds a gone device and `Reconfigure` replays it as `Present`. Shipped
providers raise the two in order on one thread. Noted on #201, not filed separately.

**1.5 Low. D1 widens the reach of an ungated `Activated`. PLAUSIBLE.** Linux
`HandleBind` (`LinuxDeviceMonitorProvider.cs:297-307`) raises `Activated` without checking
`IsActive`; `_knownConnectedIds` then stamps every later truthful `IsActive = false`
presence payload to true, and on Windows nothing retracts it short of removal. ADR-0087
acknowledges this as "not zero". Not filed.

### Reverted-hunk checks

Each new test in #193 fails against its own hunk and none passes both ways.

| Hunk reverted | Tests that fail |
|---|---|
| `ReconcileActivity` removed from `OnProviderAppeared` | `RawAppearedEvent`, both `LateAppeared*` |
| `supersededByLiveStream` forced false | `StaleActivePayloadFromWalk` |
| Cache prune on `Disappeared` dropped | `ReconfigureAfterARemoval` |
| Walk-path reconcile dropped | `StaleInactiveWalkPayload` |
| `NoteLiveStreamHandled` added to `OnProviderActivated` | `ActivityEdgeDuringTheWalk`, `StaleInactiveWalkPayload` |

### What checked out

Locks are short. No event is raised and nothing is awaited under a lock.
`_snapshotInFlight` moves with its set. The walk's unguarded `FanOutActivated` (line 1130)
can double-fan, but `ApplyConnected` is idempotent. Linux and macOS receive the fix and
show no regression beyond 1.2. The 35 lines in #195 are XML remarks on `Appeared` and
`Activated` only, and the 10 lines in #186 are comment-only.

### Verdicts

`351139b` sound as labelled. `0b93f0f` needs the follow-ups above; 1.2 and 1.3 are in
#210, 1.1 is on #201. `bfcbd4e` (watcher part) sound.

---

## 2. Windows provider: #186

Files: `src/Periphery/Windows/WindowsDeviceMonitorProvider.cs`,
`src/Periphery/Windows/DevNodeHelper.cs`,
`tests/Periphery.Tests/Platform/WindowsMonitorProviderPresenceEdgeTests.cs`.

All six new tests ran for real on the review machine (Windows 11): they picked a classed
devnode and an active monitor, and none took the silent-return path.

### Findings

**2.1 Medium. The null-ClassGuid guard drops live `Appeared` for devnodes that are
permanently classless and never start. CONFIRMED.** The guard at lines 569 to 575 assumes
"no ClassGuid" means install is unfinished. On the review machine 11 of 335 non-monitor
devnodes are permanently classless with status OK. Started ones are rescued by the
action-8 fallback; one that never starts (the driverless "unknown device", which is the
case the guard was written for) never gets a live `Appeared`, while `Devices.Enumerate()`
and the watcher's startup walk report it with `Category = All`. Confirmed by driving
action 7 for such a devnode through `OnDeviceNotification`: zero `Appeared`. Removal is
consistent (never cached, so no orphan `Disappeared`), so it is a live/snapshot asymmetry.
Filed as #209.

**2.2 Medium. Test device selection is machine-dependent. PLAUSIBLE, mechanism
confirmed.** Lines 74 to 81 pick `FirstOrDefault` from `Devices.Enumerate()` without
requiring `ClassGuid is not null`. On a machine whose first resolvable devnode is
classless, two of the six tests fail through 2.1. The tests also `return` rather than
skip when no device or monitor exists, so a headless runner reports them as passing.
In #209.

**2.3 Low. Monitor first-sighting enrichment is unproven for a real hot-plug. PLAUSIBLE.**
`TryEnrichDisplayConfig` (lines 359 to 372) calls `QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS)`
at action-7 time, before the display driver has started the panel, so a newly plugged
monitor is unlikely to be an active path yet and its first `Appeared` can still carry a
null `DisplayResolution`. The new test uses a monitor already active in the current
config, so it proves enrichment runs, not that a hot-plug payload is populated.
`QueryDisplayConfig` plus EDID reads now run on the cfgmgr32 callback thread for every
monitor action 7 and 8, including re-enumerations. Not filed; needs a bench check with a
physical monitor.

**2.4 Low. The seed-to-register gap turns a miss into a later duplicate `Appeared`.
PLAUSIBLE.** A device arriving between the seed walk and `CM_Register_Notification`
(lines 174 to 236) is absent from the provider cache but reported by the watcher's
snapshot. On its next re-enumeration (driver reload, sleep-resume) `TryAdd` succeeds, the
provider raises `Appeared`, and `DeviceWatcher.OnProviderAppeared` has no already-present
guard, so consumers get a second `Appeared`. Not filed.

**2.5 Info.** `DevNodeHelper.cs:540-561` says `CmNotifyHandle` unregisters "even if the
owning provider is not explicitly disposed", but `_selfHandle` is a normal `GCHandle`
rooting `this`, so an undisposed provider never finalizes. Pre-existing. ADR-0012 line 48
still describes the provider as using `CM_NOTIFY_FILTER_TYPE_DEVICEINTERFACE`. Noted on
#208.

### Reverted-hunk checks

| Hunk reverted | Tests that fail |
|---|---|
| `DeviceAppeared` raise in `HandleInstanceEnumerated` (lines 597 to 598) | 2 of 6 |
| First-sighting raise in `HandleInstanceStarted` (lines 505 to 510) | 1 of 6 |

The characterisation test and the naming tripwire pass either way, as labelled. The
null-ClassGuid guard has no test, as the commit message says.

### What checked out

Every function deleted from `DevNodeHelper.cs` has no remaining caller in any `src/` or
`tests/` project. Registration lifetime is correct: one handle, disposed in both
`DisposeAsync` and `CleanupAfterFailedStart`, the `SafeHandle` is idempotent, the blocking
unregister precedes `GCHandle.Free`, and there is no double-unregister path. The callback
is a static `[UnmanagedCallersOnly]` function pointer, so no delegate can be collected. A
single arrival raises exactly one `Appeared`, decided under `_cacheLock`. No residual
device-interface path remains. `Disappeared` without `Appeared` cannot happen:
`HandleInstanceRemoved` returns on a cache miss. Both paths key on the raw instance id.
Bluetooth is unchanged: the instance filter delivers nothing for link drops, this commit
adds `Appeared` on pairing only, and nothing assumes evented Bluetooth activity. No new
Windows-version-gated API.

### Verdict

The commit delivers live `Appeared` on Windows for any devnode that has a ClassGuid at
action 7 or subsequently starts. The gap is policy, not mechanics.

---

## 3. Treehopper control service: #196, #197

Files: `src/Periphery.Treehopper.Control/TreehopperControlService.cs`,
`tests/Periphery.Treehopper.Control.Tests/HotplugEdgeBindingTests.cs`.

Baseline 47 of 47. Moving the I/O half back onto `Appeared` fails three tests
(`Activated_ReadsTheVersion`, `Appeared_ListsTheBoardWithoutOpeningIt`,
`AppearedThenActivated_ReadsTheVersionExactlyOnce`), which matches the commit's
negative-control claim.

### Findings

**3.1 Medium. The session close on `Deactivated` and both generation guards are
unpinned. CONFIRMED.** Deleting the `Deactivated` subscription (line 145) and both guards
(lines 462 and 492) leaves 47 of 47 green. `OpenSessionAsync` (lines 376 to 393) goes
through `Devices.Enumerate()` and `TreehopperBoard.OpenAsync` with no injection point, so
`_session` is always null under the harness and `OnDeactivated` returns at line 494. The
seam added "to make the second change verifiable" cannot reach it. Filed as #205.

**3.2 Medium. A failing version read on `Activated` is lost silently. CONFIRMED.** With a
throwing `readVersion` delegate the board lists with `Version = null`, `LastError = null`,
`Firmware = Unknown`, one `TaskScheduler.UnobservedTaskException` fires, and the gate is
released. `TryReadVersionAsync`'s catch (lines 543 to 548) wraps only `UsbDevice.OpenAsync`;
the `Devices.Enumerate()` calls at lines 541 and 378 are outside any catch, and
`RunExclusiveAsync` (lines 562 to 569) catches only `OperationCanceledException`. No
`OperationFailed` is applied. Filed as #206.

**3.3 Low. Startup and `RefreshBoards` still open on presence. PLAUSIBLE.** `StartAsync`
(lines 136 to 137) and `ExecuteAsync` (line 184) version-read every enumerated board with
no `IsActive` gate. Bounded because the watcher's startup walk raises `Activated` and
`OnActivated` re-reads, which is why every board opens twice at startup. Already #200.

**3.4 Low. The guards defend an ordering the runtime does not produce, and miss one it
could.** The justification (lines 52 to 58) is that `SemaphoreSlim.WaitAsync` is not
FIFO; the runtime serves async waiters in FIFO order and the service has no synchronous
waiters. Under the guards' own premise: `_sessionGeneration` bumps only on open (line 389)
and `EnsureSessionAsync` (line 370) short-circuits on `_session?.Id == id`, so an
`Activated(X)` for a re-plugged board running ahead of the queued `Deactivated(X)` would
keep the dead session, the `Deactivated` would then close it, and nothing reopens it. In
#205.

**3.5 Low. `VersionReadTimeout` is never applied.** `TreehopperControlOptions.cs:25`
documents a 3 s deadline; nothing reads it. Pre-existing, but #196 moves the read under
the intent gate, so a wedged open now blocks every intent. Filed as #207.

### What checked out

Handles open only from `OnActivated` via `ReconcileSessionAsync` and from user intents;
`OnAppeared` (lines 434 to 439) applies `BoardDiscovered` only. `Activated` before
`Appeared` is fine. Two sessions for one board cannot open: every open is under the gate.
`CloseSessionAsync` (lines 395 to 408) nulls `_session` before awaiting and disposes
once. No `await` inside a lock, no `async void`. The seam is an internal constructor with
four nullable delegates, each falling through to the real call; no production timing or
ordering changes when they are null. The tests drive the real service through a real
`DeviceWatcher` and a manual monitor, not a reimplementation.

### Verdicts

`d616ef8` correct and now pinned by `efa1ece`'s tests; the failure path is silent (3.2).
`efa1ece`: the seam is clean, the `Deactivated` close is plausibly right but unverifiable
as shipped (3.1), and "implements ADR-0088" is over-claimed for the close half.

---

## 4. FlashAnything readiness gate: #194

Files: `src/Periphery.FlashAnything/TrackerDeviceWaitSource.cs`,
`tests/Periphery.FlashAnything.Tests/`.

Baseline 122 of 122. With the 12-line production hunk reverted to `!= Absent`,
`TrackerDeviceWaitSourceReadinessTests` fails 3 of 3.

### Findings

**4.1 High. macOS HID devices may never reach `Active`. PLAUSIBLE, needs a Mac.**
`MacOSDeviceProvider.cs:242` sets `IsActive` from the presence of an IOKit `sessionID`
property, with forced-true overrides for battery, network, display and serial (lines 292
to 324) and none for `IOHIDDevice`. The EFM8 bootloader is a HID device on macOS. If the
node does not publish `sessionID`, the provider raises `Appeared` but never `Activated`,
and after this commit every app-mode flash waits the full `BootloaderTimeout`. Filed as
#203, behind #107.

**4.2 Medium. An `Activated` whose payload says `IsActive = false` wedges the gate shut.
CONFIRMED mechanism.** `DeviceTrackerResolution.Resolve()` (lines 279 to 284) reports
`Active` only if the latched snapshot has the flag; `OnProviderActivated` stores the
payload as-is. `Appeared(inactive)` then `Activated(inactive)` leaves the tracker
`Present` forever. On Windows the flag comes from a devnode status read that returns
false on any failure. Filed as #202.

**4.3 Medium. The same defect class remains one layer up. PLAUSIBLE.**
`FlashAnythingService.cs:301` admits `Present`, stores the payload, and on first
detection calls `MaybeAutoflash` (line 366), which opens the device (line 524). For a
target already in bootloader mode that is an open on presence. Filed as #204.

**4.4 Low. The debounce baseline moved from "present at arm" to "active at arm."
CONFIRMED at source level.** A device `Present` before `StartAsync` is not replayed; when
it activates after arm it is delivered as a post-arm appearance, so with the default
`FirstAppearance` mode a sibling caught between enumerate and start is adopted rather
than debounced. The window is milliseconds. Not filed.

### What checked out

Timeout and cancellation are unchanged: present-never-active ends in a timeout, not a
hang. `MultiDeviceTracker.Subscribe` registers and captures state under one lock, then
replays, so an already-active device is not missed. The fake raises `Appeared(inactive)`
then `Activated(active)` on one thread, matching Linux `HandleAdd`, macOS
`OnDeviceMatched`, and the Windows callback order. Linux serial devices raise `Activated`
on add.

### Verdict

The diff does what it claims for Windows and Linux and the tests pin it. The gate now
trusts the provider's flag with no fallback (4.2), and macOS HID may be unable to satisfy
it at all (4.1).

---

## 5. ADRs, CHANGELOG, and cross-references: #188, #195, #192

- ADR-0087 stale in three places (1.3). Fixed in #210.
- ADR-0088 was still Proposed while fully implemented; the compliance table credited
  #196 alone for a decision half-landed in #197; the description of the pre-fix control
  service was in the present tense; the body did not say that XML remarks landed with it.
  Fixed in #210.
- #192 repointed 46 references and left one: the investigation's first link had the new
  number as text and the old number in the href. Fixed in #210.
- No CHANGELOG entry existed for #186, #193, #194, #196 or #197. Added in #210.
- `docs/ARCHITECTURE.md` describes the interface-change callback #186 deleted and
  promises "no device is missed", which ADR-0087 D2 no longer guarantees. Filed as #208.
- No ADR index or README exists under `docs/adr/`, so nothing needed updating there.
  ADR-0087's `depends_on`, branch and commit references resolve.

---

## Verdict by commit

| Commit | Verdict |
|---|---|
| `1c5b8a6` #190 | Documentation only. |
| `6903106` #192 | Sound; one href missed, fixed in #210. |
| `ebaa05a` #194 | Sound on Windows and Linux; macOS unverified (#203); gate needs a fallback (#202). |
| `d616ef8` #196 | Sound; failure path silent (#206). |
| `efa1ece` #197 | Seam sound; the close is unpinned (#205). |
| `351139b` #195 | Sound; ADR status corrected in #210. |
| `8e129fb` #188 | Sound; ADR drift corrected in #210. |
| `0b93f0f` #193 | Needs follow-up: #210 for `KnownDevices`, #201 for the fan-out window. |
| `bfcbd4e` #186 | Delivers live `Appeared`; classless devnodes (#209). |

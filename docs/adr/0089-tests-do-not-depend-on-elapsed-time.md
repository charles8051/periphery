---
title: "ADR-0089: Tests do not depend on elapsed time"
status: "Accepted"
status_note: "The ban ships with the 11 test projects that had violations opted out. Taking them off is tracked in #244."
date: "2026-09-13"
authors: "@charles8051"
tags: ["testing", "decision", "time", "ci", "standards", "adr-0052"]
supersedes: ""
superseded_by: ""
depends_on: ["0052-periphery-treehopper-pure-core.md", "0057-linux-extension-backends.md", "0060-device-reset-and-recovery-escalation.md", "0065-camera-testing-seam.md"]
---

# ADR-0089: Tests do not depend on elapsed time

**Tracks:** `tests/Directory.Build.targets`, `tests/BannedSymbols.txt`, every project under `tests/`, and the production types those tests wait on
**Depends on:** ADR-0052 (timing lives in the shell), ADR-0057 (rig tests are gated), ADR-0060 (the proxy's recovery timing), ADR-0065 (the camera fake)

---

## Status

**Accepted.** The ban is on for every test project. The 11 projects that had violations when it landed
are opted out and come off one at a time (#244).

The rule set is the one frame-flow adopted in its
[ADR-0072](https://github.com/charles8051/frame-flow/blob/main/docs/adr/ADR-0072-tests-do-not-depend-on-elapsed-time.md),
so the two repositories share it. Where periphery's layout differs, this record says so.

---

## Context

### The flakes

Four open issues are tests that fail on a slow runner and pass on a re-run:

- #1: `BootloaderEntryOrchestratorVerificationTests` fires a device after `Task.Delay(20)`, racing the
  orchestrator's own wait.
- #62: `DeviceProxyBaseTests.FaultedNode_ResettableFault_ResetsThenReachesActive_WithoutPriorOpen`.
- #125: `BufferExhaustionTests.LatestWins_CountsTheDrop_WhenTheConsumerHoldsMoreThanItsAllowance` on Linux CI.
- #199: `OpenSurvivesDwell_ClearsBudget_AndLastOpenFault` waits out a real 120 ms dwell.

Test comments record more that were fixed one at a time: `Stm32SerialSyncTests` ("failed on a loaded CI
runner"), `ProbeRepeatTests`, `HotplugEdgeBindingTests`, `PipeSerializationTests`, `ReadinessPollTests`,
`DeviceSessionHostReconnectPolicyTests`.

### Two dependencies with one name

A test depends on elapsed time in two different ways.

- **Time as a value.** "What time is it", "has this deadline passed". An injected clock solves it.
- **Progress.** "Has the worker finished reacting to what I just did". An injected clock does not touch
  it. `CameraSessionClockTests` already drives a `FakeTimeProvider`, and still loops `Advance` then
  `Task.Delay(10)`, because nothing tells the test that the session has armed its next timer.

Most of the 94 sites are the second kind. With the analyzer on, they break down as:

| Kind | Sites | Example |
| --- | --- | --- |
| poll against a deadline, or a delay before a positive assertion | 38 | `HotplugEdgeBindingTests`: `DateTime.UtcNow.AddSeconds(5)`, then `Task.Delay(5)` in a loop |
| delay before a negative assertion | 26 | `AutoflashServiceTests`: `await Task.Delay(200); Assert.Empty(opens)` |
| a fake holding a call open for a real duration | 11 | `FakeStm32Bootloader` holds its ACK 400 ms past a 250 ms deadline |
| `Task.Delay(Timeout.Infinite, ct)` as a park | 8 | `TestUsbBackend` wedges a transfer until cancelled |
| elapsed time measured and asserted | 4 | `ReadinessPollTests`: `Stopwatch`, then `InRange(80 ms, 5 s)` |
| race amplification, rig delays, a safety net | 7 | `DeviceWatcherThreadSafetyTests`: `Task.Delay(5)` before a dispose |

They sit in 32 files across 11 of the 24 test projects. The other 13 have none. That was not a rule
anyone wrote down, and nothing kept it. #242, merged while this record was in review, added one more:
a 200 ms delay before asserting that a reset did not happen.

### Production has no clock to fake

ADR-0052 puts timing in the shell. Most shells here take the machine's clock directly, so a test has
nothing to advance.

- `DeviceProxyBase`, the base of every proxy, waits with six `Task.Delay` calls and a
  `Environment.TickCount64` deadline. ADR-0060 records that shape. Its overridable `TimeSpan`
  properties let a test shrink a window, not skip it, so the tests sleep through it.
- `BootloaderEntryOrchestrator` settles for a fixed 750 ms and bounds its wait with `CancelAfter`.
- `Stm32SerialProgrammer` settles on a `Stopwatch` and bounds every command with `CancelAfter`.
- `ReadinessPoll` takes a `TimeProvider`. Its only production caller, `WindowsDeviceReset`, passes none
  and also waits with `Task.Delay` and `Environment.TickCount64`.
- `FlashAnythingService` and `TreehopperControlService` expose no signal that their workers are idle.

A component that takes a provider for one wait and uses the machine's clock for another is worse than
one that takes none. A fake provider moves half of it, and the test passes for the wrong reason.

### The repository already enforces drift this way

`RigCollectionConventionTests` fails when a hardware test lacks `Category=Integration`. ADR-lint fails an
ADR without frontmatter. Test timing had no equivalent.

---

## Decision

### D1: Wall time is `TimeProvider`, faked with `FakeTimeProvider`

Code that needs the current time, a timer, a delay or a deadline takes a `TimeProvider`, defaulting to
`TimeProvider.System`. Tests pass `FakeTimeProvider` from `Microsoft.Extensions.TimeProvider.Testing`,
which every test project references.

A component's waits and its elapsed-time reads come from the same provider. `CancelAfter` on an
existing `CancellationTokenSource` always uses the system timer. A deadline that has to be fakeable is
`new CancellationTokenSource(delay, timeProvider)`, linked in.

A test does not hand-roll a `TimeProvider` that advances. A double that only records what was asked
of it, and fires nothing, is fine.

This reaches production. A conversion that finds a component waiting on the machine's clock gives it a
`TimeProvider` in the same unit of work. For `DeviceProxyBase` that replaces the timing mechanism
ADR-0060 describes; the decisions there are unchanged.

### D2: A test about a background worker waits on a signal from the worker

When the code under test works on another task, the test does not guess how long that takes. It awaits
something the worker completes: a `TaskCompletionSource` in a fake, an event the component already
raises (`DeviceOpened`, `StateChanged`), a park or drain signal, or a no-op sent through the same queue
as a barrier.

The signal has to come from the state the assertion reads. A counter incremented before the state
commits releases the test early.

A negative assertion ("X did not happen") after a fixed delay passes whether or not the code is right,
if X is merely late. Wait for the worker to park, or for a barrier queued behind the action, then
assert synchronously. Where the path is synchronous, assert straight after the call.

`Task.Delay(Timeout.Infinite, ct)` in a fake is a park, not a duration. It becomes a never-completing
`TaskCompletionSource` awaited with `WaitAsync(ct)`.

### D3: Assert the decision, not its timing consequence

When behaviour follows from a choice the code makes, assert the choice. `ReadinessPollTests` times a
real give-up with a `Stopwatch`; with a `FakeTimeProvider` the test advances past the budget and asserts
the result.

### D4: Health gates assert counts and conservation

A test that asserts a run was healthy does not bound a value derived from elapsed time. It asserts
accounting that holds at any speed: frames produced equal frames delivered plus frames dropped, opens
equal closes.

### D5: Timeouts that bound a failure stay

`Task.WaitAsync(TimeSpan)`, `new CancellationTokenSource(TimeSpan)` and `CancelAfter` are not banned.
Tests use them as safety nets, and a correct run finishes far inside them. The line is whether the
duration bounds a failure or is the assertion:

> Would the test still be correct if this timeout were ten times longer?

If yes, it stays. If no, it is a sleep with different spelling and falls under D2 to D4.

Proving a negative through a timeout is not allowed. `Assert.ThrowsAsync<TimeoutException>(() =>
task.WaitAsync(100 ms))` is `Task.Delay(100)` followed by `Assert.False(task.IsCompleted)`. Against a
regression that wrongly completes the task at 150 ms, it passes at 100 ms and fails at 1000 ms. Wait for
the worker to park and assert `task.IsCompleted` is false synchronously (D2), or advance a
`FakeTimeProvider` to just short of the deadline and assert the same.

`LinuxUsbIntegrationTests` has one of each. `QemuHid_InterruptRead_CancelsPromptly` times a cancelled
read with a `Stopwatch` and asserts under 5 s. A broken wake-up path never returns, so `WaitAsync(5 s)`
expresses the same check and is still correct at 50 s. The dispose test asserts disposal took under
2 s, and the drain's give-up path waits exactly 2 s. At 20 s a broken drain would pass. That is a timing
consequence, and under D3 the test asserts which path the drain took.

The analyzer cannot tell these apart, because they are the same call. Review holds this boundary.
The same is true of `SemaphoreSlim.Wait(TimeSpan)`, `ManualResetEventSlim.Wait(TimeSpan)`, and a test
that waits out a production component's real timer without calling a banned API.

### D6: What may read the clock

Frame-flow exempts its integration test project, which measures real playback durations. Periphery has
no integration project. Its rig tests are `Category=Integration` methods inside the unit projects,
the Linux ones gated by `PERIPHERY_LINUX_DEVICE_TESTS=1` (ADR-0057). None of the five
`Category=Integration` sites needs to read the clock: two hang bounds (D5), a disposal bound that should
assert the drain's path (D3), a 100 ms wait after `DeviceWatcher.StartAsync`, which already returns with
its snapshot complete, and a 300 ms wait that can count frames instead.

- **A test that has to measure real time against real hardware** goes in a dedicated rig test project,
  opted out permanently with its reason. It does not get an exemption inside a unit project.
- **A type whose defining property is timing** carries `#pragma warning disable RS0030` at the top of
  its test file, under a comment giving that reason. The rest of its project stays under the ban.

A file that has not been converted yet belongs on the project opt-out list, where it is expected to
come off, not behind a pragma.

### D7: A converted or new test is shown to catch its bug

Before claiming a test catches a defect, put the defect back in production code, run the test, and
confirm it fails for that reason. The PR says what was injected and what failed. Restore from a copy,
not `git checkout`, which reverts the fix along with the injection.

A rig test also needs a positive control: make the helper that reaches hardware throw, and confirm the
test goes red. The `if (!Enabled) return;` guard reports a test that never ran as passed, so a negative
control alone does not show a rig test reached the device.

### D8: The build enforces it

`tests/Directory.Build.targets` adds `Microsoft.CodeAnalysis.BannedApiAnalyzers` to every project under
`tests/` and makes RS0030 an error. `tests/BannedSymbols.txt` bans:

- `Thread.Sleep`
- the `Task.Delay` overloads that do not take a `TimeProvider`
- `DateTime.Now`, `DateTime.UtcNow`, `DateTimeOffset.Now`, `DateTimeOffset.UtcNow`
- `new Stopwatch()`, `Stopwatch.StartNew`, `Stopwatch.GetTimestamp`, `Stopwatch.GetElapsedTime`
- `Environment.TickCount`, `Environment.TickCount64`
- the `System.Threading.Timer` and `System.Timers.Timer` constructors
- the `PeriodicTimer` constructor that does not take a `TimeProvider`

Each entry's message names the replacement and cites this record. `TimeProvider.CreateTimer`,
`new PeriodicTimer(TimeSpan, TimeProvider)` and `TimeProvider.System` stay allowed.

A project opts out by setting `PeripheryBanWallClockInTests` to `false` in its `.csproj`, with a comment
saying why. The list only shrinks. A new test project starts under the ban without anyone deciding to
put it there.

The ban covers tests only. Production code keeps `TimeProvider.System` as its default, and D1 is held by
review there.

---

## Consequences

### Positive

- A new dependency on elapsed time in a test fails the build with a message naming the replacement.
- Every exception is a line in a `.csproj` or the head of a test file, with its reason, and the whole set
  is one grep (CONTRIBUTING.md).
- The production conversions give consumers what the tests needed: a clock they can fake in
  `DeviceProxyBase` and the bootloader shells, as `CameraSession` and `Efm8BootloaderUploader` already have.

### Negative

- 11 projects stay opted out until someone converts them. The analyzer holds the line; it does not move it.
- Signals for idle and drained workers are internal API on production types, and each one is work.
- `DeviceProxyBase` gains public surface for its `TimeProvider`.

### Neutral

- Where frame-flow's integration project is permanently exempt, periphery has no exempt project today.

---

## Alternatives considered

### Retry flaky tests

Rejected. A retry hides a flake without stopping it, and a race in the code under test looks exactly like
a flake.

### Write the policy down and rely on review

Rejected as sufficient, kept as necessary. The tree reached 94 sites with review as the only check.

### Exempt `Category=Integration` files by pragma

Rejected. The trait marks tests that need hardware, not tests that measure time. A pragma keyed to it
would exempt the rig's assertions about content and ordering, which have no reason to read the clock.

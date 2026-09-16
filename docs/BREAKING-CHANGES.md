# Breaking changes

Each release that changes what a caller sees gets a section here, newest first. An
entry says what changed, who it affects, and what to write instead. Entries within a
release are ordered by how likely you are to hit them.

A removed or changed signature fails your build, so it announces itself. A behaviour
change does not, so behaviour changes are listed too. [CHANGELOG.md](../CHANGELOG.md)
is the full record of each release. Releases before `v4.2.0-alpha.1` are described only
there.

## `v4.2.0` — since `v4.2.0-alpha.1`

### 1. A proxy whose session keeps refaulting now gives up

`DeviceProxyBase` restarted `RecoveryContext.Attempt` at 1 whenever a reopen succeeded,
including a reopen whose session faulted again straight away. Recovery policies that
read `Attempt` now see the count keep rising until a session survives the stable-open
dwell, or until the device is replugged after giving up.

> **This one does not announce itself.** A device whose session opens and then faults
> used to be retried at `baseDelay` forever. With `ExponentialBackoffRecoveryPolicy`
> and a `maxAttempts` set, it now backs off, reaches that limit, and the proxy moves to
> `GaveUp`, as the policy's documentation always said.

With `EscalatingResetRecoveryPolicy` such a device now climbs the reset ladder, where
it used to repeat the first step forever. If you depended on endless retries, leave
`maxAttempts` null or raise it. Otherwise handle `ConnectionState.GaveUp`, which a
replug clears.

### 2. Bluetooth LE devices on Windows report `BusType.Bluetooth`

Windows uses five Bluetooth enumerators, and only `BTHENUM` was mapped. Devices under
`BTHLE`, `BTHLEDEVICE`, `BTHHFENUM` and `BTH` reported `BusType.Unknown`, and every
Bluetooth LE peripheral is among them.

`WithBusType(BusType.Bluetooth)` now returns those devices. Code that found them by
looking for `BusType.Unknown` stops finding them. `DeviceCategory` was already correct
and is unchanged, so a filter on `OfCategory(DeviceCategory.Bluetooth)` alone sees no
difference.

### 3. A wedged camera teardown no longer refuses the device forever

`PendingTeardowns` refused every open of a device whose previous teardown had been
abandoned, until that teardown completed. A native call that never returns never
completes, so the device stayed refused for the life of the process (issue #221).

The refusal now expires: 60 seconds from the most recently abandoned step, and at most
5 minutes from the first. Past that the open is allowed through, and
`CameraTeardownPendingException` is not raised.

> **This one does not announce itself.** Code that treated the exception as terminal —
> logging it and abandoning the device, or reporting the camera as failed — now sees the
> open proceed instead, and whatever the still-wedged driver does with it. That is the
> intended outcome, because the alternative was an outage no caller could end, but an
> open that used to fail fast and cheaply can now block in the driver.

`Completion` is unchanged: the registry entry outlives the refusal, so a caller that
awaits it still waits for the real teardown. An open allowed through past the refusal
increments `periphery.camera.teardown_refusals_expired` and logs at Warning naming the
parked steps, which is how to tell this case from a camera that cannot produce the
format.

The exception's message no longer advises replugging the camera. On Windows the device
instance id commonly survives a re-enumeration, so the returning camera hashes to the
same registration and is refused on arrival.

### 4. `CameraSession.OpenAsync` can refuse after configuring the device

It was the only open path that did not recheck the registry after its last device
access, so a teardown abandoned during `ConfigureAsync` could still hand back a session.
It now throws `CameraTeardownPendingException` there, as `CameraDevice.OpenSessionAsync`
already did. `CameraSessionBuilder`, and therefore `CameraDeviceProxy`, take this path.


## `v4.2.0-alpha.1` — since `v4.1.0-alpha.2`

No public signature in a published package was removed or changed. The public API
files, generated at both tags, show additions only. Every entry below is a behaviour
change.

### 1. A throwing event handler no longer propagates

`DeviceWatcher`, `DeviceTracker` and `MultiDeviceTracker` now invoke each subscriber of
`Appeared`, `Disappeared`, `Activated`, `Deactivated`, `PropertyChanged` and
`StateChanged` separately. An exception from one is logged at Error and counted, and the
remaining subscribers still run. Before, it escaped into the thread that raised the
event: usually the platform provider's notification callback, and during
`DeviceWatcher.StartAsync` the start itself.

> **This one does not announce itself.** `StartAsync` used to throw when a handler threw
> during the initial enumeration. It now succeeds, and a handler failure that reached
> your error path through that exception is logged instead.

If a handler's failure has to stop something, catch it inside the handler and act on it
there. To see isolated faults without reading logs, collect the `Periphery` meter:
`periphery.events.handler_faults` counts subscriber exceptions, tagged with
`periphery.event`.

### 2. `Appeared` fires for live arrivals on Windows

On Windows, only the startup snapshot raised `Appeared`. A device plugged in later raised
`Activated` and nothing else. `Appeared` now fires for every arrival, when the device
enters the device tree, which can be before its driver has started.

A handler that opens the device on `Appeared` could previously count on the driver being
ready on Windows, because the event never fired early there. It can now run against a
device that cannot be opened yet. Open on `Activated`, which fires once the driver has
started:

```csharp
// Before: worked on Windows only because Appeared never fired for a live arrival
watcher.Appeared += (_, e) => Open(e.Device);

// After
watcher.Activated += (_, e) => Open(e.Device);
```

Linux and macOS already raised `Appeared` for live arrivals. This brings Windows in line.

### 3. `DeviceQuery` yields as it goes unless `OrderBy` is present

Enumerating a `DeviceQuery` collected every match before yielding the first. It now
yields each match as the provider produces it. `FirstOrDefaultAsync` and `AnyAsync`
stop at the first match, `Take(n)` stops after the *n*th, and leaving an `await foreach`
early stops the provider. `OrderBy` still collects everything, because a sort has to.

Every terminal returns what it did before. What changes is timing: with `await foreach`,
you can receive devices before a fault or cancellation partway through the walk
surfaces. If you need all or nothing, use `ToListAsync`, which still returns the whole
list or throws.

### 4. Opening a camera whose teardown was abandoned throws `CameraTeardownPendingException`

Camera teardown runs each native step with a time budget and abandons a step that
overruns, which a wedged driver can cause. A new open of the same device used to start
while the abandoned step was still running, and failed later in ways that looked like
stream faults. `CameraDevice.OpenAsync`, `OpenSessionAsync` and `ReadSnapshotAsync` now
refuse at once with `CameraTeardownPendingException`, a `CameraException` subtype.

Code that catches `CameraException` already catches it. To wait instead of failing,
await the exception's `Completion` task and retry. If the driver never lets go,
`Completion` never finishes. `PendingFor` says how long the teardown has been running.

### 5. Treehopper identity writes are limited in UTF-8 bytes

`TreehopperBoard.UpdateNameAsync` and `UpdateSerialAsync` rejected more than 60
characters. They now reject more than 61 UTF-8 bytes, which is what the board's flash
record holds. A 61-character ASCII name is now accepted. A non-ASCII name of 60
characters or fewer can now throw `ArgumentOutOfRangeException`.

```csharp
if (Encoding.UTF8.GetByteCount(name) > 61)
    // shorten it before calling UpdateNameAsync
```

### 6. A `DeviceFilter.Where` predicate that throws on a watcher event is not rethrown

A predicate that throws while a watcher is processing an event no longer propagates.
The answer used in its place depends on the event. For an arrival, the device does not
match, so nothing is announced. For a removal, the device matches, so a device you were
told about is never left present forever. Each fault is counted on
`periphery.events.filter_faults`.

`DeviceQuery` and `DeviceWatcher.KnownDevices` still let the exception through. Write
predicates that answer for every device: `d => d.Name?.StartsWith("Acme") == true`
rather than `d => d.Name.StartsWith("Acme")`.

### Not breaking, but worth knowing

- **A `DeviceWatcher` whose `StartAsync` threw can be started again.** The retry used to
  throw `InvalidOperationException`. With a caller-supplied `IDeviceMonitorProvider`
  instance, a retry after the registration succeeded still cannot work, because the
  instance cannot be reset. The new `DeviceWatcher(IDeviceProvider,
  Func<IDeviceMonitorProvider>)` constructor creates one per attempt.
- **The initial snapshot raises `Activated` once per device.** A device that arrived
  during the walk used to get it twice.
- **`WithAllTags` and `WithAnyTag` copy their array.** Changing the array after the call
  no longer changes the filter.
- **An abandoned camera teardown is reported through the session's `ILogger`**, at
  Warning, instead of `Console.Error`. It is counted on
  `periphery.camera.teardowns_abandoned`, and `periphery.camera.abandoned_teardown_ms`
  records how long the step ran past its budget.
- **`TreehopperControlService` reads a board's version on `Activated`.** A board plugged
  in while the service is running now lists with its version.

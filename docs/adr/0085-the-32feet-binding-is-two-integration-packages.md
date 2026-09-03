---
title: "ADR-0085: The 32feet binding is two integration packages, and neither of them is Periphery.Bluetooth"
status: "Proposed"
status_note: "Package shapes and TFM matrix measured against the shipped 32feet assemblies (InTheHand.Net.Bluetooth 4.2.1, InTheHand.BluetoothLE 4.0.44) on 2026-09-02. No library code written. scratch/BluetoothAssetProbe covers the classic package on hardware; scratch/BleAssetProbe covers BLE asset selection with no hardware."
date: "2026-09-02"
authors: "@charles8051"
tags: ["architecture", "decision", "bluetooth", "ble", "extension", "integration-package", "32feet", "packaging", "tfm"]
supersedes: ""
superseded_by: ""
depends_on: ["0024-extension-package-pattern.md", "0026-enricher-io-boundary.md", "0054-windows-property-freshness-events-over-polling.md", "0079-port-path-is-a-parsed-value.md", "0083-ble-identity-does-not-survive-repairing.md"]
---

# ADR-0085: The 32feet binding is two integration packages, and neither of them is `Periphery.Bluetooth`

## Status

Proposed. Resolves the question "should `Periphery.Bluetooth` just be a wrapper
over 32feet?" with: yes to the binding, no to that name, and no to it being one
package.

---

## Context

Periphery discovers Bluetooth devices today. `DeviceCategory.Bluetooth`
enumerates on all three platforms and the README lists it as supported. What
Periphery cannot do is *talk* to one: no RFCOMM stream, no GATT read, no radio
control, no pairing.

32feet.NET is the obvious binding. It is the only maintained .NET Bluetooth
library with real cross-platform reach, and it is MIT, so depending on it raises
no licence conflict with Periphery's PolyForm Small Business terms.

Three things about the current state make the shape of the binding non-obvious.

### 1. Windows gives no Bluetooth liveness events

Measured 2026-08-30 against a paired BR/EDR keyboard over a power-off/power-on
cycle. `DeviceInfo.IsActive` on the `BTHENUM\DEV_…` node tracked the link
correctly in both directions, agreeing with 32feet's
`BluetoothDeviceInfo.Connected` within ~2 s. `DeviceWatcher` raised **zero**
edges for either transition — no `Deactivated`, no `Activated`, no
`Disappeared`, no `PropertyChanged`. Starting a watcher cold while the link was
down, so the id was absent from `DeviceWatcher.OnProviderActivated`'s
`_knownConnectedIds` dedup set, still saw nothing on reconnect. cfgmgr32 does not
push `DEVICEINSTANCESTARTED` for a BR/EDR link change on an already-installed
devnode.

ADR-0054 records `DeviceDeactivated` as never raised on Windows. The reconnect
raises nothing either. So Bluetooth activity on Windows is poll-only, the
README's flagship real-time-monitoring example does not fire for a link toggle,
and a 32feet-backed poll is currently the *only* live Bluetooth signal available
on that platform. That is the strongest argument for the binding, and it is
independent of any I/O use case.

### 2. 32feet is two packages, not one

| NuGet package | Latest | Scope | Key types |
|---|---|---|---|
| `InTheHand.Net.Bluetooth` | 4.2.1 | BR/EDR | `BluetoothClient`, `BluetoothDeviceInfo`, `BluetoothListener`, `BluetoothRadio`, `BluetoothSecurity`, SDP `ServiceRecord` |
| `InTheHand.BluetoothLE` | 4.0.44 | GATT | `Bluetooth`, `BluetoothDevice`, `GattService`, `GattCharacteristic`, `GattDescriptor`, `BluetoothLEScanFilter` |

Separate version lines, disjoint namespaces (`InTheHand.Net.*` vs
`InTheHand.Bluetooth`), disjoint APIs. The classic package has no GATT client:
its only LE-adjacent symbols are `_isLowEnergy`, `IsLowEnergySupported`, and
`StartAdvertising` / `StopAdvertising` on the radio. The BLE package models the
Web Bluetooth API and has no RFCOMM.

The split is visible at runtime, not just in the symbol table. On a machine with
one bonded BR/EDR keyboard and one bonded LE mouse,
`BluetoothClient.PairedDevices` returned **one** device — the keyboard. The LE
mouse has a `BTHLE\DEV_…` node that Periphery enumerates and that the D5 join
parses cleanly, and the classic package does not see it on any of its three
Windows assets. A single package covering "Bluetooth" would therefore be silently
blind to half the peripherals on this machine.

### 3. Their TFM matrices are incompatible, and the BLE one does not fit Periphery's

Periphery multi-targets `net8.0;net10.0` — bare, no OS-specific TFM.

Assemblies inspected directly for provider symbols:

| Package | Asset a bare TFM resolves to | What is actually in it |
|---|---|---|
| `InTheHand.Net.Bluetooth` 4.2.1 | `net8.0` | BlueZ **and** `bthprops.cpl` — Linux and Win32 both present, and Win32 is the one that runs on Windows (measured, §5) |
| `InTheHand.BluetoothLE` 4.0.44 | `net9.0` (for `net10.0`) | BlueZ only — `Linux.Bluetooth`, `Tmds.DBus`, `org.bluez` |
| `InTheHand.BluetoothLE` 4.0.44 | `netstandard2.0` (for `net8.0`) | **no provider at all** — `PlatformNotSupportedException` and nothing else |
| `InTheHand.BluetoothLE` 4.0.44 | `net9.0-windows10.0.19041` | the real one — `Windows.Devices.Bluetooth.GenericAttributeProfile` |

The classic package works on Periphery's TFMs as they stand. The BLE package does
not: `net8.0` gets a stub that throws everywhere, and `net10.0` gets the Linux
provider, so a Windows consumer would get BlueZ code on Windows. There is no
`net8.0` asset and no bare Windows asset. Reaching 32feet's WinRT GATT
implementation requires an OS-specific TFM.

### 4. The bare TFM drags a Linux D-Bus chain with a live CVE — on both packages

Measured by restoring each TFM and reading `dotnet list package --include-transitive`:

| TFM | Transitive, beyond the logging abstractions |
|---|---|
| `net10.0` | `Linux.Bluetooth` 5.67.1, `Tmds.DBus` 0.20.0 |
| `net10.0-windows` | none |
| `net10.0-windows10.0.19041` | none |

`Tmds.DBus` < 0.92.0 carries CVE-2026-39959, high severity, CVSS 7.1 — bus-peer
signal spoofing, file descriptor exhaustion, and a malformed-body crash. Fixed in
0.92.0, which 32feet has not taken. Restore emits NU1903 for it.

This is a property of the **bare** TFM, not of the BLE package: the table above
was measured against `InTheHand.Net.Bluetooth` 4.2.1, the classic one.
`InTheHand.BluetoothLE` 4.0.44 pulls the identical chain on a bare target. Every
bare-TFM consumer of either package gets the Linux D-Bus stack, on every
platform, because a bare asset has to carry the Linux provider to work on Linux
at all. The OS-specific Windows assets carry no provider they do not need, so
they carry no D-Bus.

### 5. The classic package's bare asset drives Win32, measured

`scratch/BluetoothAssetProbe` builds one source file against all three TFMs, so
the only variable is the resolved asset. Run on Windows 11, 2026-09-02, against a
bonded Keychron K4 (BR/EDR) and a bonded iClever LE mouse:

| | `net10.0` (bare, 150,528 B) | `net10.0-windows` (Win32, 125,952 B) | `net10.0-windows10.0.19041` (WinRT, 124,928 B) |
|---|---|---|---|
| `BluetoothRadio.Default` | works | works | works |
| radio `Name` | `WS-HOME` | `WS-HOME` | `WS-HOME - Front` |
| `LmpVersion` | `Version21` | `Version21` | `Version50` |
| `Manufacturer` | `Intel` | `Intel` | `Unknown` |
| `PairedDevices` | 1 (the K4) | 1 (the K4) | 1 (the K4) |

The bare asset is byte-for-byte a different assembly from the `windows7.0` one
(distinct MVIDs, 24 KB apart) and reports the radio identically. The WinRT asset
diverges on all three radio fields. So the bare asset drives Win32 on Windows,
and the `bthprops.cpl` string was not a false lead.

That also means the two Windows assets are not interchangeable for radio
metadata. `LmpVersion` is `Version21` from Win32 and `Version50` from WinRT for
the same radio, and the Win32 answer is the wrong one — 32feet's own docs say the
Win32 path "can only determine version up to Bluetooth 2.1". A consumer reading
`LmpVersion` gets a different answer depending on the TFM the *package* was built
for, which is not a choice they can see.

### 6. `net10.0-windows` gets the Linux BLE provider, and it throws on Windows

Context §3 read the BLE package's assets statically. `scratch/BleAssetProbe`
executes the three cases — no hardware, availability only:

| TFM | Asset | Provider | `Bluetooth.GetAvailabilityAsync()` |
|---|---|---|---|
| `net10.0` | 89,600 B | BlueZ (`Linux.Bluetooth`, `Tmds.DBus`) | `ConnectException: No path specified for UNIX transport` |
| `net10.0-windows` | 89,600 B, **same MVID** | BlueZ | same `ConnectException` |
| `net10.0-windows10.0.19041` | 93,696 B | WinRT (`Microsoft.Windows.SDK.NET`, `WinRT.Runtime`) | `true` |

`net10.0-windows` is not a partial win over the bare target. It is byte-identical
to it. Upstream's only Windows BLE asset is `net9.0-windows10.0.19041`, which a
`net10.0-windows7.0` project cannot consume, so NuGet falls all the way back to
the bare asset — and that asset reaches for a D-Bus socket on Windows.

The same fallback governs the dependency graph, and it does **not** behave the
same way for the two packages. Measured by restoring a project at each TFM
against the 32feet package directly:

| Consumer TFM | Package | `Linux.Bluetooth` + `Tmds.DBus`? |
|---|---|---|
| `net8.0` | `InTheHand.Net.Bluetooth` | yes |
| `net8.0-windows` | `InTheHand.Net.Bluetooth` | **no** |
| `net10.0-windows` | `InTheHand.BluetoothLE` | **yes** |

NuGet picks a dependency group by the *consuming project's* framework, not by
which `lib/` asset an intermediate package resolved. So a consumer's own TFM
decides their exposure, and a wrapper cannot hand them a cleaner graph than their
TFM earns. The classic package has a Windows dependency group at `net8.0-windows`
and is clean there; the BLE package's only Windows group is at 10.0.19041, so
every Windows consumer below that version gets the vulnerable chain *and* the
broken provider.

---

## Decision

### D1 — The 32feet binding never ships as `Periphery.Bluetooth`

ADR-0024's dependency table gives `Periphery.{Domain}` no third-party runtime
dependency beyond `Microsoft.Extensions.Logging.Abstractions`. A package named
`Periphery.Bluetooth` that references 32feet breaks that row, and the row is not
decoration: it is what lets a consumer take a Periphery extension without
inheriting a vendor's dependency graph — in this case one carrying a live CVE.

The binding is a `Periphery.{Domain}.{Library}` integration package. Routing it
through [`integration-package-placement.md`](../patterns/integration-package-placement.md):
Q1 is no, it needs no Periphery `internal`; Q2 is no, 32feet defines no item or
ownership protocol a Periphery type would want to implement — `BluetoothClient`
hands back a `NetworkStream` and `BluetoothDevice` is a leaf; so Q3 applies and
it lives in this repo as an opt-in leaf package.

### D2 — Two packages, named for the two NuGet packages

```
Periphery.Bluetooth.InTheHand  ->  InTheHand.Net.Bluetooth  (BR/EDR, RFCOMM, SDP, radio, pairing)
Periphery.Ble.InTheHand        ->  InTheHand.BluetoothLE    (GATT client, LE scan)
```

One third-party package each, so the "one library it is named for" rule holds as
written with no amendment. The domain segment differs because the domains differ:
ADR-0083 already treats LE identity as its own problem, and the two transports
land on different devnode shapes (`BTHENUM\DEV_…` vs `BTHLE\DEV_…`).

Merging them into one package was considered and rejected on the TFM evidence in
Context §3, not on the naming rule. One package cannot carry both a
Periphery-standard bare TFM set and the OS-specific TFM the BLE half needs
without shipping a Windows GATT surface that silently resolves to BlueZ.

### D3 — `Periphery.Ble.InTheHand` targets `net10.0-windows10.0.19041`, and drops `net8.0`

The TFM set is `net10.0;net10.0-windows10.0.19041`. The bare target carries the
Linux path under `[SupportedOSPlatform]` guards; the Windows target carries WinRT
GATT. This is ADR-0018's isolation pattern applied to a package rather than an
assembly.

`net8.0` is dropped from this package alone. There is no `net8.0` asset upstream
and the `netstandard2.0` fallback is a throw-only stub, so a `net8.0` target
would ship a package that compiles and then fails at first call on every
platform. The other packages keep `net8.0;net10.0` per ADR-0069.

**A `net10.0-windows` target is added, and its only job is to fail loudly.**
Context §6 measured what happens without one: NuGet hands that consumer the bare
BlueZ asset, the package compiles, and the first call throws
`ConnectException: No path specified for UNIX transport`. Offering the 10.0.19041
asset does not prevent that — nothing about a *missing* TFM stops NuGet falling
back to a compatible one.

So the TFM exists, carries no BLE surface, and fails at build with a message
naming what to do:

```xml
<Target Name="BleRequiresWindows10" BeforeTargets="Build"
        Condition="'$(TargetFramework)' == 'net10.0-windows'">
  <Error Text="Periphery.Ble.InTheHand needs net10.0-windows10.0.19041 or later on
               Windows. 32feet's only Windows GATT asset targets 10.0.19041, so an
               unversioned Windows target resolves the Linux BlueZ provider and
               throws at first call. Raise your TargetFramework." />
</Target>
```

A build error a consumer can act on beats a Unix-socket exception at run time on
a machine with no Unix sockets. The alternative — say nothing and let it throw —
was rejected because the failure gives no hint that the TFM is the cause.

`Periphery.Bluetooth.InTheHand` needs none of this to *function* — its bare asset
works on Windows. D4 gives it a Windows TFM anyway, for a different reason.

### D4 — Both packages carry a Windows TFM, and the advisory is surfaced rather than suppressed

An OS-specific Windows TFM is not only how the BLE package reaches WinRT GATT; it
is also how a Windows consumer avoids `Linux.Bluetooth` and `Tmds.DBus` 0.20.0.
So `Periphery.Bluetooth.InTheHand` takes one too, even though its bare asset
works on Windows:

```
Periphery.Bluetooth.InTheHand   net8.0;net10.0;net10.0-windows
Periphery.Ble.InTheHand         net10.0;net10.0-windows;net10.0-windows10.0.19041
```

**The claim this decision can support, stated exactly.** Context §6 measured that
NuGet selects a dependency group by the *consumer's* TFM. So the packages do not
control anyone's dependency graph; the consumer's own TFM does, and these TFM
sets decide only what a consumer is able to reach:

| Consumer targets | Classic package | BLE package |
|---|---|---|
| `net8.0` / `net10.0` (bare) | vulnerable chain | vulnerable chain |
| `net8.0-windows` | clean | n/a — package has no `net8.0` |
| `net10.0-windows` | clean | **build error** (D3), because the alternative is a broken provider *and* the chain |
| `net10.0-windows10.0.19041` | clean | clean |

An earlier draft of this decision claimed "a Windows consumer of either package
inherits no `Tmds.DBus`". That was wrong for the BLE package below 10.0.19041,
which is what D3's build error now closes.

`net10.0-windows` rather than `net10.0-windows10.0.19041` for the classic
package. Two reasons, and one of them cuts the other way. In favour: it binds the
same Win32 provider the bare asset already runs (Context §5), so adding the TFM
changes the dependency graph and nothing else, and it asks nothing of the
consumer's SDK beyond the default Windows targeting pack. Against: the WinRT
asset reports the radio better — `Version50` and a real `Manufacturer` where
Win32 gives `Version21` and `Intel` from a table capped at Bluetooth 2.1.

`net10.0-windows` wins on the grounds that the classic package's job is RFCOMM
and pairing, not radio telemetry, and that no-behaviour-change is the cheaper
thing to reason about. If radio metadata later matters, the fix is a
`net10.0-windows10.0.19041` TFM alongside, not instead.

Neither package adds `NoWarn` for NU1903 and neither pins a transitive override.
A consumer on the bare TFM — which on Linux is the only option — sees the
advisory, and that is the correct outcome: the vulnerability is real there,
Periphery cannot fix it, and the upgrade is 32feet's to take. Both package
READMEs state the exposure and that it follows the bare TFM rather than the
package.

A consumer who needs it silenced can pin `Tmds.DBus` 0.92.0 themselves. Doing
that on their behalf would be asserting a compatibility claim across a
0.20 → 0.92 jump that has not been tested here.

### D5 — `BluetoothAddress` is a parsed value in `Periphery` core

Both integration packages need the same join: instance ID to BD_ADDR.

```
^BTH(ENUM|LE)\\DEV_(?<address>[0-9A-Fa-f]{12})
```

Verified 2026-08-30: on a machine with seven `OfCategory(Bluetooth)` nodes it
matched exactly one, and that one was the paired device. It is currently the only
key available, because `DeviceInfo.MacAddress` is null for Bluetooth —
`WindowsNetworkEnricher` is scoped to the Net class GUID — and `SerialNumber` is
never populated on a Bluetooth node.

This is a parsed value over a platform identifier, which is the shape ADR-0079
settled for `PortPath`. It goes in `Periphery` core beside `PortPath`,
`HardwareId`, and `SerialPortName`, not in an extension. Each integration
converts it to its own vendor type at the boundary
(`InTheHand.Net.BluetoothAddress`, `InTheHand.Bluetooth.BluetoothDevice`), and no
Periphery type gains a 32feet base or member.

`BluetoothAddress` carries ADR-0083's durability caveat in its `<remarks>`: it is
address-derived, and on an LE peripheral using a resolvable private address it
does not survive a re-pair. It is a join key, not an identity.

### D6 — No `Periphery.Bluetooth` domain package is created

The table's integration row says `Periphery.{Domain}.{Library}` depends on
`Periphery.{Domain}`. Both packages here depend on `Periphery` core directly
instead. This is a deviation, and the justification is that the domain package
would be empty: D5 puts the only shared vocabulary in core, and the I/O surfaces
have nothing in common — an RFCOMM stream and a GATT characteristic share no
base.

ADR-0026 sketches a `BluetoothPort.ReadAttributesAsync(DeviceInfo)` returning
`BluetoothDeviceAttributes`, and ADR-0024's spoke-to-spoke section names a
hypothetical `Periphery.Gatt` building on `Periphery.Bluetooth` handle
infrastructure. Neither is built, and neither is contradicted here — if a
first-party Bluetooth I/O surface is ever written, it takes the
`Periphery.Bluetooth` name and these two packages become leaves under it. Until
then the name stays unclaimed rather than being spent on a wrapper.

---

## Consequences

### Positive

- Windows gains a live Bluetooth liveness signal for the first time. A poll over
  `BluetoothDeviceInfo.Connected`, joined by D5, closes the gap in Context §1 —
  measured to agree with `IsActive` in both directions.
- A Windows consumer who targets a Windows TFM can reach a clean graph — no
  `Tmds.DBus`, no CVE — via D4's table. A consumer of `Periphery` core inherits
  nothing either way.
- The BLE package's worst failure mode is now a build error naming the fix,
  instead of a Unix-socket exception on a Windows machine.
- The BR/EDR half works on Periphery's existing bare TFM set; the Windows TFM D4
  adds to it is a dependency-hygiene measure, not a correctness one.

### Negative

- Two packages for what a consumer thinks of as one subject. Someone wanting
  "Bluetooth" has to know which transport their device speaks. Package
  descriptions and the README have to carry that.
- `Periphery.Ble.InTheHand` has a TFM set no other package in the repo has, and a
  Windows-only capability gap that is invisible at compile time on the bare
  target.
- Periphery now ships two packages with a known-vulnerable transitive dependency
  on their bare TFM, disclosed rather than fixed. On Linux that TFM is the only
  one, so a Linux consumer has no way to opt out.
- macOS gets BLE (via the BLE package's Apple assets, untested here) and no
  BR/EDR — `InTheHand.Net.Bluetooth` has no macOS asset at all.

### Neutral

- NativeAOT behaviour is unmeasured for both packages. The WinRT asset goes
  through CsWinRT, which ADR-0016 already deals with for enrichment; the Win32
  P/Invoke path in the classic package should be friendlier. Neither is a claim
  until measured.

---

## Alternatives considered

**One package, `Periphery.Bluetooth`, referencing both 32feet packages.** What
was asked for. Rejected twice over: it breaks ADR-0024's dependency row for the
`Periphery.{Domain}` name, and independently of any rule, the TFM matrices do not
compose — see D2.

**One integration package, `Periphery.Bluetooth.InTheHand`, referencing both.**
Would need the "one library it is named for" rule re-read as "one vendor
project". Rejected on TFM grounds alone, which makes the rules question moot.

**Vendor a minimal first-party BR/EDR stack instead.** Win32 `bthprops` plus
BlueZ D-Bus is a large surface that is testable only on hardware, and 32feet has
done it for fifteen years under MIT. Not worth writing.

**Wait for 32feet to add a `net10.0` and a bare-Windows asset.** Would remove D3
entirely. Not something to block on; D3 is reversible if it lands.

---

## Open questions

Two of the four are closed by Context §5 and the numbers below; two remain.

**Closed — the bare asset drives Win32.** Context §5. D4's Windows TFM on the
classic package stays a hygiene measure rather than becoming a correctness one,
and it acquires a second justification: the Win32 and WinRT assets disagree about
`LmpVersion` and `Manufacturer`, so which one the package binds is a decision
worth making deliberately.

**Closed — a poll costs nothing worth counting.** `Refresh()` plus `Connected`
over one paired device: p50 0.3 ms, p95 0.9 ms, max 0.9 ms over 20 cycles. At a
2 s cadence this is not a reason to make polling opt-in. Whether it stays flat
across a dozen paired devices is unmeasured but not in doubt at this magnitude.

**Open — LE agreement.** Does `BluetoothDeviceInfo.Connected` agree with
`IsActive` on an LE peripheral? Still unanswered, and now known to be
unanswerable with the classic package: it does not enumerate the LE mouse at all
(Context §2). The question moves to `Periphery.Ble.InTheHand`. `--watch` in the
probe measures the BR/EDR half today.

**Open — macOS BR/EDR.** No answer here. Whether the gap matters depends on
demand that does not exist yet.

---

## References

### ADRs

- [ADR-0018: WinRT enrichment TFM coupling](0018-winrt-enrichment-tfm-coupling.md) — the OS-specific-TFM isolation pattern D3 applies
- [ADR-0024: Extension package pattern](0024-extension-package-pattern.md) — the dependency table D1 and D6 answer to
- [ADR-0026: Enricher I/O boundary](0026-enricher-io-boundary.md) — sketches `BluetoothPort.ReadAttributesAsync`
- [ADR-0054: Windows property freshness — events over polling](0054-windows-property-freshness-events-over-polling.md) — the `DeviceDeactivated` gap Context §1 extends
- [ADR-0069: Restore net8 TFM](0069-restore-net8-tfm-untested.md) — the TFM set D3 departs from
- [ADR-0079: Port path is a parsed value](0079-port-path-is-a-parsed-value.md) — the precedent D5 follows
- [ADR-0083: BLE identity does not survive re-pairing](0083-ble-identity-does-not-survive-repairing.md) — the durability caveat D5 carries

### Patterns

- [`integration-package-placement.md`](../patterns/integration-package-placement.md) — the Q1/Q2/Q3 routing D1 runs

### External

- [32feet.NET](https://github.com/inthehand/32feet) — MIT
- [`InTheHand.BluetoothLE` on NuGet](https://www.nuget.org/packages/InTheHand.BluetoothLE)
- [GHSA-xrw6-gwf8-vvr9 / CVE-2026-39959](https://github.com/advisories/GHSA-xrw6-gwf8-vvr9) — `Tmds.DBus` < 0.92.0

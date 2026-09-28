# BLE bench testing

> **When to read this:** you're implementing or changing the LE side of
> [ADR-0083], [ADR-0085] or [ADR-0090], and need to know which hardware proves
> it, what each board is for, and in what order to measure before building.
>
> **Status:** partly done. The DK firmware is built and runs on the DK, and step 2 is
> measured. Step 1 is half done: `BluetoothAddress` shipped, and ADR-0090's table is
> deferred. Steps 3 to 5 are still a plan. Both boards are flashed. The test harness below
> does not exist yet.

The bench is two Nordic LE boards acting as test peripherals. The host's own
Bluetooth radio and OS stack are what's under test. The harness sends each
transition to the DK over its serial console, and the DK's log
confirms that the transition happened. The Thingy runs on its own.

This is the Bluetooth companion to [`usb-lifecycle-testing.md`](usb-lifecycle-testing.md).
There, the harness makes a USB device disappear. Here, it makes an LE peripheral
disconnect, reboot, change address, or forget its bond.

---

## Hardware

| Board | Zephyr target | Role | How it is flashed |
|---|---|---|---|
| nRF52833 DK (PCA10100) | `nrf52833dk/nrf52833` | Primary test peripheral. Programmer for the Thingy. | Onboard SEGGER J-Link, over the same USB cable. No bootloader involved. |
| Thingy:52 (nRF52832) | `thingy52/nrf52832` | Second peripheral. Battery-powered, so its power switch drops the link without a command. | SWD only, from the DK's Debug out connector or a standalone J-Link. |
| nRF52840 Dongle | n/a | Sniffer. Not owned yet. | Sniffer firmware from nRF Sniffer for Bluetooth LE. |

The DK's onboard J-Link means `west flash` or `nrfutil device program` needs no
external probe.

The Thingy:52 has no USB data line. It is programmed over its 2x5 1.27 mm SWD
header, from a standalone J-Link or from the DK's Debug out connector. Neither board
ships with the cable. While a cable is attached to Debug out, the DK's J-Link drives
the Thingy instead of the DK's own chip. A standalone J-Link avoids that.

The Thingy:52 ships with an nRF5 SDK Secure DFU bootloader that updates over BLE.
It accepts only packages signed with Nordic's key, so custom firmware has to go
in over SWD, and that erases the bootloader. Nordic's
[Thingy:52 firmware repository][thingy52-fw] has the stock image for restoring
it. Before it is wiped, the stock Thingy is a live Secure DFU target over BLE.
That is the protocol the [Nordic DFU spec] covers, and Nordic's released Thingy
DFU packages are signed for it.

nRF Sniffer supports the nRF52833 DK up to hardware version 2. Nordic has reported
the version 3 interface IC as incompatible. The version is on the DK's label.
Whatever the version, the DK can't sniff while it is the test peripheral. An
nRF52840 Dongle avoids both problems, and it is the same hardware the
[Nordic DFU spec]'s OQ-2 needs.

Host tooling: the nRF Connect SDK (`west`), `nrfutil`, and the SEGGER J-Link
software.

---

## Coverage

Nordic parts are LE only. That sets the bench's reach.

| Work | Bench covers it? |
|---|---|
| LE enumeration on Windows (`BTHLE\DEV_…` nodes) | Yes |
| LE liveness pushes on Windows (the open questions in [the OS APIs exploration]) | Yes |
| ADR-0085 D5 join for LE; `ToBluetoothDeviceAsync`; GATT through 32feet | Yes |
| [#232], address-key durability per LE address type | Yes |
| ADR-0083 NEG-005, two identical LE units present at once | Yes, with both boards on the same firmware |
| ADR-0090, if the liveness measurements leave LE without an OS push | Yes |
| BR/EDR: `RfcommDuplexPipe`, `BluetoothDeviceProxy`, the classic join | No. Needs a BR/EDR peripheral. An original ESP32 running ESP-IDF's `bt_spp_acceptor` example is the cheapest. The ESP32-S3 and C3 are LE only. |
| Linux, [#258] | No. The bench boards work, but the Linux device rig has no radio. It needs a USB Bluetooth adapter. |
| macOS, [#259] | No. Needs a Mac with a radio. CI runners have none. |

---

## Peripheral firmware

[`scratch/BleBenchFirmware`](../../scratch/BleBenchFirmware) is a Zephyr application: the
Bluetooth shell, the Heart Rate service (`0x180D`) notifying once a second, and a `bench` shell
command set. Heart Rate carries a notify, a read and a write characteristic for GATT work.

`build.ps1` builds every image into `C:\blebench\images`:

| Image | Board | Use |
|---|---|---|
| `nrf52833dk-bench.hex` | DK | Harness-driven peripheral on a static random identity |
| `nrf52833dk-bench-privacy.hex` | DK | The same with `CONFIG_BT_PRIVACY=y` and a 30 s RPA timeout |
| `nrf52833dk-peripheral-hr.hex` | DK | Zephyr's `peripheral_hr` as "Periphery Bench HR", for step 4 |
| `thingy52-peripheral-hr.hex` | Thingy:52 | The same image for the Thingy |
| `sniffer_nrf52833dk_nrf52833_4.1.1.hex` | DK | nRF Sniffer, copied from the `nrfutil ble-sniffer` install |

The Thingy:52 has no serial console, so it runs `peripheral_hr`, which advertises at boot. Its
power switch is its only control.

Flash with `nrfutil device program --firmware <hex> --serial-number <J-Link serial>`. The
first SWD flash of a Thingy:52 needs `--options chip_erase_mode=ERASE_ALL`, which erases the
stock image, its bootloader and the UICR. Back the stock image up first with
`nrfutil device dump-to-file <file>.hex --code --uicr`. `nrfutil device recover` is needed only
when `nrfutil device protection-get` reports readback protection. The nRF52833 DK here had it on;
the Thingy:52 here did not.

| Transition | Command |
|---|---|
| Start the stack, after every reset | `bt init` |
| Advertise on an identity address | `bench adv start identity [id]` |
| Advertise on a rotating RPA (privacy image) | `bench adv start rpa [id]` |
| Stop advertising | `bench adv stop` |
| Drop the link from the peripheral side | `bt disconnect` |
| Forget the host's bond | `bt clear all` |
| Create a new identity | `bt id-create [addr]` |
| Show privacy, identities and the advertising address | `bench status` |
| Reboot the peripheral | `nrfutil device reset`, through the J-Link |

The reboot goes through the J-Link rather than the shell, so it still works when the firmware
is hung. It resets the DK only while the Thingy's SWD cable is detached. Bonds and identities
are stored in flash, so they survive the reboot.

### Address types

- **Static random** is the default. Nordic parts derive one from FICR at boot.
  `bt id-create <addr>` adds a chosen one.
- **Resolvable private** is the privacy image with `bench adv start rpa`. The advertising
  set is created without `BT_LE_ADV_OPT_USE_IDENTITY`.
- **Public** is not available. Nordic parts have no factory public address, and neither image
  sets one. Zephyr's vendor HCI command Write BD_ADDR is the likely route. Untried.

### Ground truth from the peripheral

Every event a harness waits on is a log line from the `bench` module:

| Line | When |
|---|---|
| `bench: ready board=… privacy=…` | Boot |
| `bench: adv started mode=… id=…` | `bench adv start` succeeded |
| `bench: adv address <addr> (random)` | After each start, and after each RPA rotation |
| `bench: rpa expired` | The RPA timeout fired |
| `bench: connected peer=… err=…` | A link came up |
| `bench: disconnected peer=… reason=…` | A link went down |
| `bench: pairing complete peer=… bonded=…` | Pairing finished |
| `bench: peer identity resolved rpa=… identity=…` | The host's own RPA was resolved |
| `bench: bond deleted id=… peer=…` | `bt clear` removed a bond |

The host resolves RPAs and never shows the rotation, so the `adv address` lines are the RPA
column's record of each on-air address. A sniffer capture confirms them independently. [#232]
already notes that a passing RPA column without that record is not trustworthy.

---

## Harness

The test project reaches the DK's shell through its J-Link VCOM port (SEGGER
VID `0x1366`), opened with `Periphery.Serial`. The DK may expose more than one
VCOM port, so use the one that answers `bench status`.

A small interface keeps tests independent of the board behind them:

```csharp
/// <summary>One LE test peripheral, driven over its shell.</summary>
public interface IBlePeripheralFixture : IAsyncDisposable
{
    /// <summary>The address the host will key the bond on, as the peripheral reports it.</summary>
    Task<BluetoothAddress> ReadIdentityAsync(CancellationToken ct = default);

    Task AdvertiseAsync(LeAddressMode mode, CancellationToken ct = default);
    Task StopAdvertisingAsync(CancellationToken ct = default);
    Task DisconnectAsync(CancellationToken ct = default);
    Task ClearBondsAsync(CancellationToken ct = default);
    Task ResetAsync(CancellationToken ct = default);

    /// <summary>Completes when the peripheral logs a line matching <paramref name="pattern"/>.</summary>
    Task<string> WaitForLogAsync(Regex pattern, CancellationToken ct = default);
}
```

`WaitForLogAsync` is the barrier. A test waits for the peripheral's own line,
such as `Disconnected` or a new RPA, and never for elapsed time. That follows
[ADR-0089].

The host side pairs and unpairs without a person:

- pair with `DeviceInformationCustomPairing.PairAsync(DevicePairingKinds.ConfirmOnly)`,
  accepting in `PairingRequested`;
- unpair with `DeviceInformationPairing.UnpairAsync()`.

This should run unattended from a desktop process. It has not been measured.

### Rules

These are the same rules the Linux device rig follows.

- Tests run only when `PERIPHERY_BLE_DEVICE_TESTS=1` is set. When it is set, a
  missing peripheral is a failure, not a skip.
- Every assertion that the host raised nothing is paired with a peripheral log
  line showing the transition happened. Without that pairing, the test passes
  whenever the peripheral is off.
- Measurement runs are not tests. A probe that answers an open question records
  its result in a document. It becomes a test only once the result is a
  contract Periphery keeps.

---

## Order

1. **No hardware.** Unit-test the pure pieces: D5's `BluetoothAddress` parse,
   address-type classification from the top two bits, and ADR-0090 D2's rule
   that turns reported levels into edges, as a `(state, level) -> (state', edge?)`
   table.
2. **LE liveness on Windows.** Done 2026-09-28. `scratch/BleLinkHold/run-link-toggles.ps1`
   holds a GATT session open from the host and toggles the DK's link while
   `scratch/BluetoothHciEventProbe` and `scratch/BleOsProbe` run, with `-Drop disconnect` or
   `-Drop reset`. The HCI event covers LE, and core already raises the edges, so ADR-0090 has no
   motivating case on Windows. Results are in [the OS APIs exploration][the OS APIs exploration].
3. **[#232]'s matrix.** Three address types, each through four transitions:
   disconnect and reconnect, peripheral reboot, unpair and re-pair, host reboot.
   The result decides whether `BleDeviceProxy` ships or is rejected.
4. **Two identical units.** Flash `peripheral_hr` on both boards, with the same
   device name. The host then sees two peripherals that differ only by address.
   This measures what ADR-0083 NEG-005 predicts and what `DeviceGroupTracker` has
   to handle.
5. **Regression tests.** Turn the results from steps 2 to 4 that became
   contracts into gated tests.

---

## Where results go

| Result | Destination |
|---|---|
| How an OS behaves | [The OS APIs exploration][the OS APIs exploration] |
| The durability matrix | [#232] |
| A result that changes a decision | An amendment to the ADR it changes |

This document covers how to run the measurements. It does not record their
results.

---

## References

- [ADR-0083: BLE identity does not survive re-pairing][ADR-0083]
- [ADR-0085: The 32feet binding is two integration packages][ADR-0085]
- [ADR-0089: Tests do not depend on elapsed time][ADR-0089]
- [ADR-0090: Supplementary activity sources][ADR-0090]
- [Bluetooth OS APIs exploration][the OS APIs exploration]
- [Nordic DFU spec]
- [Zephyr Bluetooth shell][bt-shell], [GAP shell commands][gap-shell]
- [nRF52833 DK](https://www.nordicsemi.com/Products/Development-hardware/nRF52833-DK)
- [Thingy:52 firmware and cable programming][thingy52-fw]
- [nRF Sniffer for Bluetooth LE](https://www.nordicsemi.com/Products/Development-tools/nrf-sniffer-for-bluetooth-le)

[ADR-0083]: ../adr/0083-ble-identity-does-not-survive-repairing.md
[ADR-0085]: ../adr/0085-the-32feet-binding-is-two-integration-packages.md
[ADR-0089]: ../adr/0089-tests-do-not-depend-on-elapsed-time.md
[ADR-0090]: ../adr/0090-supplementary-activity-sources.md
[the OS APIs exploration]: ../explorations/bluetooth-os-apis-2026-09.md
[Nordic DFU spec]: ../feature-specs/firmware-flashing/nordic-dfu/spec.md
[#232]: https://github.com/charles8051/periphery/issues/232
[#258]: https://github.com/charles8051/periphery/issues/258
[#259]: https://github.com/charles8051/periphery/issues/259
[bt-shell]: https://docs.zephyrproject.org/latest/services/connectivity/bluetooth/bluetooth-shell.html
[gap-shell]: https://docs.zephyrproject.org/latest/services/connectivity/bluetooth/shell/host/gap.html
[thingy52-fw]: https://github.com/NordicSemiconductor/Nordic-Thingy52-FW

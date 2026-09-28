# BLE bench testing

> **When to read this:** you're implementing or changing the LE side of
> [ADR-0083], [ADR-0085] or [ADR-0090], and need to know which hardware proves
> it, what each board is for, and in what order to measure before building.
>
> **Status:** plan. No firmware has been flashed and no harness exists. Every
> command below is untested on this bench until a result is recorded against it.

The bench is two Nordic LE boards acting as test peripherals. The host's own
Bluetooth radio and OS stack are what's under test. The harness sends each
transition to a peripheral over its serial console, and the peripheral's log
confirms that the transition happened.

This is the Bluetooth companion to [`usb-lifecycle-testing.md`](usb-lifecycle-testing.md).
There, the harness makes a USB device disappear. Here, it makes an LE peripheral
disconnect, reboot, change address, or forget its bond.

---

## Hardware

| Board | Role | How it is flashed |
|---|---|---|
| nRF54 DK | Primary test peripheral. Programmer for the Thingy. | Onboard SEGGER J-Link, over the same USB cable. No bootloader involved. |
| Nordic Thingy | Second peripheral. Battery-powered, so it can drop the link without a command. | Depends on the model; see below. |
| nRF52840 Dongle | Sniffer. Not owned yet. | Sniffer firmware from nRF Sniffer for Bluetooth LE. |

Both nRF54 DKs carry an onboard J-Link, so `west flash` or
`nrfutil device program` needs no external probe. The nRF54L15 has no USB
peripheral, so on that DK the J-Link is the only way in.

The Thingy's flashing path depends on the model:

- **Thingy:53** ships with MCUboot and USB serial recovery. Hold SW2 while
  switching SW1 on, then write a `dfu_application.zip` with nRF Connect
  Programmer. Every build flashed this way has to keep MCUboot in the image.
- **Thingy:52** has no USB data line and is SWD only.

Either model can be programmed from the DK's Debug OUT connector over a 10-pin
1.27 mm cable. Nordic recommends keeping that cable under 8 cm on the nRF54L15 DK.
This is also the recovery path for a Thingy:53 whose MCUboot was erased.

Nordic's nRF Sniffer page lists only nRF52 boards as of 2026-09: the nRF52840
Dongle, nRF52840 DK, nRF52833 DK and nRF52 DK. The DK also can't sniff while it
is acting as the test peripheral. An nRF52840 Dongle solves both problems, and it
is the same hardware the [Nordic DFU spec]'s OQ-2 needs.

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

Zephyr's Bluetooth shell, [`tests/bluetooth/shell`][bt-shell], built with
`CONFIG_BT_HRS=y`. The shell gives the harness a command for each transition, and
Heart Rate (`0x180D`) carries a notify, a read and a write characteristic for GATT
work. The same image goes on both boards.

| Transition | Command |
|---|---|
| Advertise on the identity address | `bt advertise on identity` |
| Advertise with privacy (RPA) | `bt advertise on`, with `CONFIG_BT_PRIVACY=y` |
| Stop advertising | `bt advertise off` |
| Drop the link from the peripheral side | `bt disconnect <addr>` |
| Forget the host's bond | `bt clear all` |
| Create a new identity | `bt id-create [addr]` |
| List identities | `bt id-show` |
| Reboot the peripheral | `nrfutil device reset`, through the J-Link |

The reboot goes through the J-Link rather than the shell, so it still works when
the firmware is hung.

### Address types

- **Static random** is the default. Nordic parts derive one from FICR at boot.
  `bt id-create <addr>` sets a chosen one.
- **Resolvable private** needs `CONFIG_BT_PRIVACY=y`. Set `CONFIG_BT_RPA_TIMEOUT`
  short (for example 30 s) so a rotation happens inside one test.
- **Public** is not available by default. Nordic parts have no factory public
  address, so one has to be written with Zephyr's vendor HCI command Write
  BD_ADDR before `bt init`. Unverified on the nRF54.

### Ground truth from the peripheral

The shell prints each connection and disconnection with the peer address.
`bt id-show` prints the identity. That covers the public and static-random
columns.

For the RPA column, each on-air address needs a separate record, because the
host resolves RPAs and never shows the rotation. Two sources can provide it:

- a few lines of firmware that log the new address from
  `bt_le_ext_adv_cb.rpa_expired` using `bt_le_ext_adv_get_info`;
- a sniffer capture.

[#232] already notes that a passing RPA column without this record is not
trustworthy.

---

## Harness

The test project reaches the DK's shell through its J-Link VCOM port (SEGGER
VID `0x1366`), opened with `Periphery.Serial`. The DK may expose more than one
VCOM port, so use the one that answers `bt id-show`. A Thingy:53 serves the
same shell on its USB CDC ACM console.

A small interface keeps tests independent of which board they drive:

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
2. **LE liveness on Windows.** Toggle the DK's link while
   `scratch/BluetoothHciEventProbe` and `scratch/BleOsProbe` run. Answer:
   - Does `GUID_BLUETOOTH_HCI_EVENT` fire with connection type LE?
   - Does `BDIF_LE_CONNECTED` track the link on the devnode?
   - Does `IOCTL_BTH_GET_DEVICE_INFO` list LE devices?
   - Does the AEP watcher raise `Updated` for `IsConnected`?

   If the HCI event covers LE, the provider path from [#286](https://github.com/charles8051/periphery/issues/286) extends to LE, and
   ADR-0090 has no motivating case on Windows. Do this before building ADR-0090.
3. **[#232]'s matrix.** Three address types, each through four transitions:
   disconnect and reconnect, peripheral reboot, unpair and re-pair, host reboot.
   The result decides whether `BleDeviceProxy` ships or is rejected.
4. **Two identical units.** Put the same firmware, name and GATT table on both
   boards. This measures what ADR-0083 NEG-005 predicts and what
   `DeviceGroupTracker` has to handle.
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
- [nRF54L15 DK](https://www.nordicsemi.com/Products/Development-hardware/nRF54L15-DK)
- [Developing with Thingy:53](https://developer.nordicsemi.com/nRF_Connect_SDK/doc/latest/nrf/ug_thingy53.html)
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

// Bench harness for the EP0 vendor rescue reset (periphery #226 follow-up).
//
// Sends the out-of-band reset defined in Treehopper-EFM8/inc/treehopper.h:
//     bmRequestType 0x40 (host-to-device | vendor | device)
//     bRequest      0x52 ('R')
//     wValue        0xA5A5
//
// The device resets inside the USB ISR without completing the status stage, so the
// control transfer is EXPECTED to fault or the device to vanish mid-request. That is
// success. Anything that returns cleanly means the request was ignored.
//
// Usage:  Ep0Rescue [<serial-substring>]

using Periphery;
using Periphery.Treehopper;

const ushort TreehopperVid = 0x10C4;
const ushort TreehopperPid = 0x8A7E;

var filter = args.Length > 0 ? args[0] : null;

var devices = await Devices.Enumerate()
    .WithUsbId(new HardwareId(TreehopperVid), new HardwareId(TreehopperPid))
    .Active()
    .ToListAsync();

if (filter is not null)
    devices = devices.Where(d => d.Id.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

if (devices.Count == 0)
{
    Console.Error.WriteLine("No Treehopper board found (in application mode).");
    return 1;
}

if (devices.Count > 1)
{
    Console.Error.WriteLine($"{devices.Count} boards matched; pass a serial substring to disambiguate:");
    foreach (var d in devices) Console.Error.WriteLine($"  {d.Id}");
    return 1;
}

var info = devices[0];
Console.WriteLine($"Target : {info.Id}");

// The STATIC rescue, not TreehopperBoard.OpenAsync + the instance method. Opening a board
// reconciles its configuration over the peripheral-config bulk endpoint, which is exactly the
// endpoint a wedged board is not draining - so the open would time out and this harness would
// fail on the only boards it is for. This overload opens the USB device and nothing else.
//
// Still goes through the library rather than a raw control transfer: the vendor bytes belong in
// Periphery.Treehopper next to RebootAsync, not duplicated in a bench harness.
await TreehopperBoard.RescueResetAsync(info);

// IMPORTANT: RescueResetAsync deliberately reports nothing, and neither should this harness
// pretend to. A device that resets mid-transfer and one that never implemented the request
// fault identically (WinUSB Win32 error 31 in both cases) - an earlier version of this file
// treated "faulted" as success and reported success against firmware with no handler at all.
//
// Watching for a USB drop does not work either: the board re-enumerates under the same
// instance id in ~224 ms, and polling misses it. (That is also why treehopper-flash reboot
// long reported "NO EFFECT" for the 0x0C opcode - the reset works, the detection did not.)
//
// The reliable check is the arrival timestamp, which survives the transient:
//   Get-PnpDeviceProperty -InstanceId <id> -KeyName DEVPKEY_Device_LastArrivalDate
// Sample it before and after; a changed value means the device re-enumerated, i.e. it reset.
Console.WriteLine();
Console.WriteLine("Rescue request sent. This tells you nothing on its own - verify by comparing");
Console.WriteLine("DEVPKEY_Device_LastArrivalDate before and after; a change means it reset.");
return 0;

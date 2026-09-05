// Reads individual flash bytes out of an EFM8 board using the bootloader's Verify (0x34) record as
// a read oracle: a one-byte range has only 256 possible CRC-16/XMODEM values, so whichever
// candidate the bootloader acknowledges IS the byte. Verify writes nothing.
using Periphery;
using Periphery.Bootloader;
using Periphery.Bootloader.Efm8.Usb;
using Periphery.Hid;
using Periphery.Treehopper.Firmware;

string? target = null;
var addrs = new List<int>();
for (int i = 0; i < args.Length; i++)
{
    if (args[i] is "--target" or "-t") target = args[++i];
    else if (args[i] is "--addr" or "-a")
        addrs.Add(Convert.ToInt32(args[++i].Replace("0x", ""), 16));
}
if (target is null || addrs.Count == 0)
{
    Console.Error.WriteLine("usage: Efm8FlashPeek --target <device-id> --addr 0x3DFF [--addr 0x0000 ...]");
    return 2;
}

var all = await Devices.Enumerate().ToListAsync();
var dev = all.FirstOrDefault(d => string.Equals(d.Id, target, StringComparison.OrdinalIgnoreCase));
if (dev is null) { Console.Error.WriteLine($"no device {target}"); return 2; }

var entry = new TreehopperBootloaderEntry();
var opts = new BootloaderEntryOptions
{
    BootloaderTimeout = TimeSpan.FromSeconds(20),
    ApplicationFilter = new DeviceFilter().WithUsbId(
        TreehopperFirmwareUpdate.ApplicationVid, TreehopperFirmwareUpdate.ApplicationPid),
    ApplicationTimeout = TimeSpan.FromSeconds(20),
};

var outcome = await BootloaderEntryOrchestrator.RunAsync(
    entry, dev,
    flash: async (boot, ct) =>
    {
        await using var hid = await HidDevice.OpenAsync(boot, ct);
        var tx = new HidEfm8Transport(hid);

        async Task<byte> SendAsync(byte[] frame)
        {
            await tx.WriteOutputReportAsync(frame, ct);
            return await tx.ReadReplyAsync(ct);
        }

        // Setup (flash keys + bank 0). Required before any other record.
        byte setupReply = await SendAsync([(byte)'$', 4, 0x31, 0xA5, 0xF1, 0x00]);
        Console.WriteLine($"Setup            -> 0x{setupReply:X2} ({(Efm8ReplyCode)setupReply})");

        foreach (int addr in addrs)
        {
            int found = -1;
            var seen = new HashSet<byte>();
            for (int v = 0; v <= 0xFF; v++)
            {
                ushort crc = Efm8BootRecordGenerator.Crc16Xmodem([(byte)v]);
                byte[] verify =
                [
                    (byte)'$', 7, 0x34,
                    (byte)(addr >> 8), (byte)addr,
                    (byte)(addr >> 8), (byte)addr,
                    (byte)(crc >> 8), (byte)crc,
                ];
                byte r = await SendAsync(verify);
                seen.Add(r);
                if (r == (byte)Efm8ReplyCode.Acknowledge) { found = v; break; }
            }
            string replies = string.Join(",", seen.Select(b => $"0x{b:X2}"));
            Console.WriteLine(found >= 0
                ? $"0x{addr:X4} = 0x{found:X2}   (replies seen: {replies})"
                : $"0x{addr:X4} = UNREADABLE - no candidate acknowledged (replies seen: {replies})");
        }

        // Leave: RunApp.
        byte leave = await SendAsync([(byte)'$', 3, 0x36, 0x00, 0x00]);
        Console.WriteLine($"RunApp           -> 0x{leave:X2} ({(Efm8ReplyCode)leave})");
        return true;
    },
    opts, flashSucceeded: static _ => true);

Console.WriteLine($"application returned: {outcome.ApplicationReturned}");
return 0;

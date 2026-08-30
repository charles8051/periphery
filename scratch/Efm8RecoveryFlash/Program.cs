// Recovery flasher: upload firmware to a Treehopper that is ALREADY in the EFM8
// HID bootloader (0x10C4:0xEAC9). The `treehopper firmware` CLI only targets
// app-mode boards (discover -> reboot -> flash), so a cold-bootloader board
// needs the generic uploader directly. Same brick-guard + uploader the CLI uses
// underneath: infer/verify the file format, convert .hex -> boot records, replay.
//
//   dotnet run --project scratch/Efm8RecoveryFlash -- [<path-to-firmware.hex/.tfi>]

using Periphery;
using Periphery.Bootloader.Efm8.Usb;
using Periphery.Hid;
using Periphery.Treehopper;

string hexPath = args.Length > 0
    ? args[0]
    : Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..",
        "src", "Periphery.Treehopper", "firmware", "Treehopper-EFM8", "build", "Treehopper.hex");
hexPath = Path.GetFullPath(hexPath);

if (!File.Exists(hexPath))
{
    Console.Error.WriteLine($"Firmware not found: {hexPath}");
    return 1;
}

Console.WriteLine($"Firmware: {hexPath}");
byte[] raw = await File.ReadAllBytesAsync(hexPath);

// Brick-guard: infer format from extension, verify against content, convert .hex.
byte[] records;
try
{
    records = Efm8FirmwareImage.ToBootRecords(raw, Path.GetFileName(hexPath), Efm8BootOptions.Ub1);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Refused: {ex.Message}");
    return 1;
}
Console.WriteLine($"Converted {raw.Length} bytes -> {records.Length} boot-record bytes.");

var info = await Devices.Enumerate()
    .WithUsbId(TreehopperFirmwareUpdate.BootloaderVid, TreehopperFirmwareUpdate.BootloaderPid)
    .Active()
    .FirstOrDefaultAsync();

if (info is null)
{
    Console.Error.WriteLine(
        $"No EFM8 HID bootloader ({TreehopperFirmwareUpdate.BootloaderVid}:" +
        $"{TreehopperFirmwareUpdate.BootloaderPid}) is present. Is the board in the bootloader?");
    return 1;
}
Console.WriteLine($"Bootloader device: {info.Id}");

await using var hid = await HidDevice.OpenAsync(info);
var transport = new HidEfm8Transport(hid);

Console.WriteLine("Flashing (erase + rewrite)...");
var progress = new Progress<Efm8UploadProgress>(p =>
    Console.Write($"\r  {p.Percent,3:0}%  record {p.RecordsSent}/{p.TotalRecords}   "));

var result = await Efm8BootloaderUploader.UploadAsync(
    transport, records, Efm8FlashConfirmation.ConfirmEraseAndReflash, progress);

Console.WriteLine();
Console.WriteLine(result.Describe());
return result.Success ? 0 : 2;

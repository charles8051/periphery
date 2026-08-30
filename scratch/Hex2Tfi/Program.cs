// Convert an Intel HEX firmware image to the EFM8 boot-record stream (.tfi) that
// the host updater bundles/replays. Uses the in-repo generator (the hex2boot
// replacement), Ub1 part profile.  Usage: Hex2Tfi <in.hex> <out.tfi>
using Periphery.Bootloader.Efm8.Usb;

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: Hex2Tfi <in.hex> <out.tfi>");
    return 2;
}

string hex = File.ReadAllText(args[0]);
byte[] records = Efm8BootRecordGenerator.FromIntelHex(hex, Efm8BootOptions.Ub1);
File.WriteAllBytes(args[1], records);
Console.WriteLine($"{args[0]} -> {args[1]}: {records.Length} boot-record bytes, " +
                  $"{Efm8Protocol.ParseRecords(records).Length} records");
return 0;

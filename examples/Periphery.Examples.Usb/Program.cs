// Periphery.Usb spike demo.
//
//   (no args)      list every USB device Periphery enumerates
//   <vid> <pid>    open the first matching device through the WinUSB backend,
//                  dump its descriptors, and run a GET_DESCRIPTOR control transfer
//
// Example — the attached Treehopper:
//   dotnet run --project examples/Periphery.Examples.Usb -- 10C4 8A7E

if (args.Length >= 1 && args[0].Equals("blink", StringComparison.OrdinalIgnoreCase))
    return await BlinkLedAsync();

if (args.Length >= 2)
    return await OpenAndDumpAsync(args[0], args[1]);

await ListAsync();
return 0;

static async Task ListAsync()
{
    var devices = await Devices.Enumerate()
        .OfCategory(DeviceCategory.Usb)
        .ToListAsync();

    Console.WriteLine($"USB devices ({devices.Count}):");
    foreach (var d in devices)
    {
        var vid = d.VendorId?.ToString() ?? "????";
        var pid = d.ProductId?.ToString() ?? "????";
        Console.WriteLine($"  {vid}:{pid}  {d.Name ?? "(unnamed)"}");
        Console.WriteLine($"            {d.Id}");
    }
}

static async Task<int> OpenAndDumpAsync(string vidArg, string pidArg)
{
    if (!HardwareId.TryParse(vidArg, out _) || !HardwareId.TryParse(pidArg, out _))
    {
        Console.Error.WriteLine($"Could not parse VID '{vidArg}' / PID '{pidArg}' — use hex, e.g. 10C4 8A7E.");
        return 2;
    }

    var matches = await Devices.Enumerate()
        .WithUsbId(vidArg, pidArg)
        .ToListAsync();

    var device = matches.FirstOrDefault();
    if (device is null)
    {
        Console.Error.WriteLine($"No USB device matching {vidArg}:{pidArg} is connected.");
        return 1;
    }

    Console.WriteLine($"Opening {device.VendorId}:{device.ProductId}  {device.Name ?? "(unnamed)"}");
    Console.WriteLine($"  {device.Id}");

    await using var usb = await UsbDevice.OpenAsync(device);

    var dd = usb.Descriptor;
    Console.WriteLine();
    Console.WriteLine("Device descriptor:");
    Console.WriteLine($"  USB version     : {dd.UsbVersion >> 8}.{dd.UsbVersion & 0xFF:x2}");
    Console.WriteLine($"  VID:PID         : {dd.VendorId}:{dd.ProductId}");
    Console.WriteLine($"  class/sub/proto : {dd.DeviceClass:X2}/{dd.DeviceSubClass:X2}/{dd.DeviceProtocol:X2}");
    Console.WriteLine($"  ep0 max packet  : {dd.MaxPacketSize0}");
    Console.WriteLine($"  device version  : {dd.DeviceVersion:X4}");
    Console.WriteLine($"  configurations  : {dd.ConfigurationCount}");

    var cfg = usb.Configuration;
    Console.WriteLine();
    Console.WriteLine($"Configuration {cfg.ConfigurationValue} (bus power {cfg.MaxPowerMilliamps} mA):");
    foreach (var iface in cfg.Interfaces)
    {
        Console.WriteLine(
            $"  Interface {iface.InterfaceNumber} (alt {iface.AlternateSetting}) " +
            $"class {iface.InterfaceClass:X2}/{iface.InterfaceSubClass:X2}/{iface.InterfaceProtocol:X2}");
        foreach (var ep in iface.Endpoints)
            Console.WriteLine(
                $"    endpoint 0x{ep.EndpointAddress:X2}  {ep.Direction,-12} {ep.TransferType,-11} " +
                $"maxPacket={ep.MaxPacketSize} interval={ep.Interval}");
    }

    // Control-transfer round-trip: re-read the device descriptor over endpoint 0
    // via a standard GET_DESCRIPTOR(DEVICE) request — proves control transfers work.
    Console.WriteLine();
    Console.WriteLine("GET_DESCRIPTOR(DEVICE) via control transfer:");
    var buffer = new byte[18];
    var setup = new UsbControlSetup
    {
        RequestType = 0x80,            // device→host, standard, recipient = device
        Request = 0x06,               // GET_DESCRIPTOR
        Value = 0x0100,               // descriptor type 1 (DEVICE) << 8 | index 0
        Index = 0,
    };
    int got = await usb.ControlTransferAsync(setup, buffer);
    Console.WriteLine($"  read {got} byte(s): {Convert.ToHexString(buffer.AsSpan(0, got))}");

    return 0;
}

// Blinks the Treehopper on-board LED through raw Periphery.Usb bulk writes — the
// hand-rolled "before" that Periphery.Treehopper will make ergonomic. Wire protocol
// is preserved from the existing Treehopper SDK:
//   peripheral-config OUT endpoint = 0x02
//   DeviceCommands.ConfigureDevice = 0x01, DeviceCommands.LedConfig = 0x0E
//   LED packet = [LedConfig, on ? 1 : 0]
static async Task<int> BlinkLedAsync()
{
    const byte PeripheralConfigEndpoint = 0x02;
    const byte ConfigureDevice = 0x01;
    const byte LedConfig = 0x0E;

    var device = (await Devices.Enumerate().WithUsbId("10C4", "8A7E").ToListAsync()).FirstOrDefault();
    if (device is null)
    {
        Console.Error.WriteLine("No Treehopper (10C4:8A7E) connected.");
        return 1;
    }

    await using var usb = await UsbDevice.OpenAsync(device);
    Console.WriteLine($"Blinking on-board LED of '{device.Name}' via raw WinUSB bulk writes…");

    // Initialise the board (high-impedance pins) — mirrors TreehopperUsb.Reinitialize.
    await usb.BulkWriteAsync(PeripheralConfigEndpoint, new byte[] { ConfigureDevice, 0x00 });

    for (int i = 0; i < 6; i++)
    {
        bool on = (i % 2) == 0;
        int n = await usb.BulkWriteAsync(PeripheralConfigEndpoint, new byte[] { LedConfig, (byte)(on ? 1 : 0) });
        Console.WriteLine($"  LED {(on ? "on " : "off")}  ({n} bytes written)");
        await Task.Delay(300);
    }

    await usb.BulkWriteAsync(PeripheralConfigEndpoint, new byte[] { LedConfig, 0x00 }); // leave it off
    Console.WriteLine("Done.");
    return 0;
}

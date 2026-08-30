using Periphery;


Console.WriteLine("╔══════════════════════════════════════════╗");
Console.WriteLine("║        Periphery — Examples              ║");
Console.WriteLine("╚══════════════════════════════════════════╝");
Console.WriteLine();

static void DumpDevice(DeviceInfo device)
{
    var physical = device.BusType is not BusType.Software and not BusType.Unknown;
    Console.WriteLine($"  {device.Name ?? "(unnamed)",-60} [{device.Category,-10}] [{device.BusType,-10}] {(device.IsActive ? "connected" : "disconnected"),-12} {(physical ? "physical" : "virtual")}");

    if (device.Manufacturer is not null)
        Console.WriteLine($"      Manufacturer  : {device.Manufacturer}");
    if (device.SerialNumber is not null)
        Console.WriteLine($"      SerialNumber  : {device.SerialNumber}");
    if (device.VendorId is { } vid)
        Console.WriteLine($"      VendorId      : {vid}  ProductId: {device.ProductId?.ToString() ?? "(none)"}");
    if (device.DriverVersion is not null)
        Console.WriteLine($"      DriverVersion : {device.DriverVersion}  Driver: {device.Driver ?? "(none)"}");
    if (device.UsbClassCode is not null)
        Console.WriteLine($"      UsbClass      : {device.UsbClassCode}  Speed: {device.UsbSpeed?.ToString() ?? "(none)"}  MaxPower: {(device.MaxPowerMilliamps.HasValue ? $"{device.MaxPowerMilliamps}mA" : "(none)")}");
    if (device.MacAddress is not null)
        Console.WriteLine($"      MacAddress    : {device.MacAddress}");
    if (device.IPAddresses is { Length: > 0 } ips)
        Console.WriteLine($"      IPAddresses   : {string.Join(", ", ips)}");
    if (device.DisplayResolution is not null)
        Console.WriteLine($"      Resolution    : {device.DisplayResolution}  Bounds: {device.DisplayBounds}");
    if (device.DriveType is not null)
        Console.WriteLine($"      DriveType     : {device.DriveType}");
    if (device.PortName is not null)
        Console.WriteLine($"      PortName      : {device.PortName}");
    if (device.LocationPath is not null)
        Console.WriteLine($"      LocationPath  : {device.LocationPath}");
    Console.WriteLine($"      PnpDeviceId   : {device.Id}");
}

// ─────────────────────────────────────────────────────────────────────
// 1. Physical devices only (excludes virtual NICs, software audio, etc.)
// ─────────────────────────────────────────────────────────────────────
Console.WriteLine("── 1. Physical Network Adapters Only ──────────────────────────");

var physicalNics = await Devices.Enumerate()
    .OfCategory(DeviceCategory.Network)
    .PhysicalOnly()
    .ToListAsync();

Console.WriteLine($"Found {physicalNics.Count} physical network adapter(s):");
foreach (var device in physicalNics)
{
    DumpDevice(device);
}

// ─────────────────────────────────────────────────────────────────────
// 2. Virtual devices only (VPN, Hyper-V, loopback, etc.)
// ─────────────────────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── 2. Virtual Network Adapters Only ──────────────────────────");

var virtualNics = await Devices.Enumerate()
    .OfCategory(DeviceCategory.Network)
    .VirtualOnly()
    .ToListAsync();

Console.WriteLine($"Found {virtualNics.Count} virtual network adapter(s):");
foreach (var device in virtualNics)
{
    DumpDevice(device);
}

// ─────────────────────────────────────────────────────────────────────
// 3. All network adapters (for comparison)
// ─────────────────────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── 3. All Network Adapters ──────────────────────────");

var allNics = await Devices.Enumerate()
    .OfCategory(DeviceCategory.Network)
    .ToListAsync();

Console.WriteLine($"Found {allNics.Count} total network adapter(s):");
foreach (var device in allNics)
{
    DumpDevice(device);
}

// ─────────────────────────────────────────────────────────────────────
// 4. Physical USB devices
// ─────────────────────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── 4. Physical USB Devices ──────────────────────────");

var physicalUsb = await Devices.Enumerate()
    .OfCategory(DeviceCategory.Usb)
    .PhysicalOnly()
    .ToListAsync();

Console.WriteLine($"Found {physicalUsb.Count} physical USB device(s):");
foreach (var device in physicalUsb.Take(10))
{
    DumpDevice(device);
}

// ─────────────────────────────────────────────────────────────────────
// 5. USB Mice
// ─────────────────────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── 5. USB Mice ──────────────────────────");

var mice = await Devices.Enumerate()
    .OfCategory(DeviceCategory.Mouse)
    .ToListAsync();

Console.WriteLine($"Found {mice.Count} USB mouse(s):");
foreach (var device in mice.Take(10))
{
    DumpDevice(device);
}


// ─────────────────────────────────────────────────────────────────────
// 6. USB keyboards
// ─────────────────────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── 6. USB Keyboards ──────────────────────────");

var keyboards = await Devices.Enumerate()
    .OfCategory(DeviceCategory.Keyboard)
    .ToListAsync();

Console.WriteLine($"Found {keyboards.Count} USB keyboard(s):");
foreach (var device in keyboards.Take(10))
{
    DumpDevice(device);
}



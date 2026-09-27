using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Periphery;

// ─────────────────────────────────────────────────────────────────────────────
// Listens for the Bluetooth driver's connect/disconnect push on every local
// radio, beside a Periphery DeviceWatcher, while you power-cycle a paired device.
//
//   dotnet run --project scratch/BluetoothHciEventProbe [seconds] [--keep-handle] [--access attributes|query|read]
//
//   seconds         how long to listen (default 120; Ctrl+C stops early)
//   --keep-handle   hold the radio handle open instead of closing it after registering
//   --access        CreateFile access for the radio: FILE_READ_ATTRIBUTES (default),
//                   query-only (0), or GENERIC_READ
//
// Output sources:
//   hci        GUID_BLUETOOTH_HCI_EVENT: a link to a remote device came up or went down
//   l2cap      GUID_BLUETOOTH_L2CAP_EVENT: a channel on that link opened or closed
//   in-range   GUID_BLUETOOTH_RADIO_IN_RANGE: the stack's flags for a device changed
//   stack      the Bluetooth stack's own flags for the hci address, from IOCTL_BTH_GET_DEVICE_INFO
//              on the radio, read just before each devnode sample (issue #288)
//   devnode    IsActive of the Bluetooth devnodes carrying the hci address, at +0 and +2000 ms
//   periphery  a live DeviceWatcher edge (the startup snapshot is counted, not printed)
// ─────────────────────────────────────────────────────────────────────────────

int seconds = args.Select(a => int.TryParse(a, out var v) ? v : 0).FirstOrDefault(v => v > 0, 120);
Probe.KeepHandle = args.Contains("--keep-handle");
int accessArg = Array.IndexOf(args, "--access");
string accessName = accessArg >= 0 && accessArg + 1 < args.Length ? args[accessArg + 1] : "attributes";
uint access = accessName switch
{
    "attributes" => Native.FILE_READ_ATTRIBUTES,
    "query" => 0u,
    "read" => Native.GENERIC_READ,
    _ => throw new ArgumentException($"--access must be attributes, query or read, not '{accessName}'"),
};

Probe.Log("probe", $"radio handle {(Probe.KeepHandle ? "held open" : "closed after registering")}, " +
                   $"access {accessName} (0x{access:X}), listening {seconds} s");

var registrations = new List<nint>();
var radios = Native.RadioInterfacePaths();
Probe.Log("probe", $"{radios.Count} radio interface(s)");

for (int i = 0; i < radios.Count; i++)
{
    int radio = i + 1;
    nint handle = Native.CreateFileW(
        radios[i], access, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
        0, Native.OPEN_EXISTING, 0, 0);
    if (handle == Native.INVALID_HANDLE_VALUE)
    {
        Probe.Log("probe", $"radio {radio}: CreateFile failed, Win32 error {Marshal.GetLastPInvokeError()}");
        continue;
    }

    int cr = Native.RegisterForDeviceHandleEvents(handle, radio, out nint notify);
    if (Probe.KeepHandle)
        Probe.HeldHandles[radio] = handle;
    else
        Native.CloseHandle(handle);

    Probe.Log("probe", $"radio {radio}: CM_Register_Notification -> {(cr == Native.CR_SUCCESS ? "CR_SUCCESS" : $"CONFIGRET 0x{cr:X2}")}, " +
                       $"handle {(Probe.KeepHandle ? "held" : "closed")}");
    if (cr == Native.CR_SUCCESS)
        registrations.Add(notify);
}

Probe.RadioPaths = radios;
Probe.StackAccess = access;
await Probe.ReportStackAtStartupAsync();

await using (var watcher = Devices.Watch().OfCategory(DeviceCategory.Bluetooth))
{
    watcher.Appeared    += (_, e) => Probe.Edge("Appeared", e.Device);
    watcher.Activated   += (_, e) => Probe.Edge("Activated", e.Device);
    watcher.Deactivated += (_, e) => Probe.Edge("Deactivated", e.Device);
    watcher.Disappeared += (_, e) => Probe.Edge("Disappeared", e.Device);
    await watcher.StartAsync();
    Probe.Live = true;
    Probe.Log("periphery", $"watcher started; {Probe.SnapshotEdges} snapshot edge(s) not printed");

    Probe.Log("probe", "power-cycle a paired device now");
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    try { await Task.Delay(Timeout.Infinite, cts.Token); }
    catch (OperationCanceledException) { }
}

foreach (var notify in registrations)
    Native.CM_Unregister_Notification(notify);
foreach (var handle in Probe.HeldHandles.Values)
    Native.CloseHandle(handle);
await Task.WhenAll(Probe.PendingReports);

Probe.Log("probe", $"stopped. device-handle events: {Probe.Summary()}");
return 0;

static class Probe
{
    static readonly Stopwatch Clock = Stopwatch.StartNew();
    static readonly ConcurrentDictionary<ulong, string> Aliases = new();
    static readonly ConcurrentDictionary<string, int> Counts = new();
    static int _nextAlias;

    public static volatile bool Live;
    public static int SnapshotEdges;
    public static bool KeepHandle;
    public static readonly ConcurrentDictionary<int, nint> HeldHandles = new();
    public static readonly ConcurrentBag<Task> PendingReports = [];

    static readonly Guid HciEvent   = new("fc240062-1541-49be-b463-84c4dcd7bf7f");
    static readonly Guid L2capEvent = new("7eae4030-b709-4aa8-ac55-e953829c9daa");
    static readonly Guid InRange    = new("ea3b5b82-26ee-450e-b0d8-d26fe30a3869");
    static readonly Guid OutOfRange = new("e28867c9-c2aa-4ced-b969-4570866037c4");

    static readonly Dictionary<Guid, string> OtherEvents = new()
    {
        [new("5dc9136d-996c-46db-84f5-32c0a3f47352")] = "AUTHENTICATION_REQUEST",
        [new("d668dfcd-0f4e-4efc-bfe0-392eeec5109c")] = "KEYPRESS_EVENT",
        [new("547247e6-45bb-4c33-af8c-c00efe15a71d")] = "HCI_VENDOR_EVENT",
    };

    // BDIF_* from bthdef.h (Windows SDK 10.0.26100.0).
    static readonly (uint Bit, string Name)[] Bdif =
    [
        (0x00000001, "ADDRESS"), (0x00000002, "COD"), (0x00000004, "NAME"), (0x00000008, "PAIRED"),
        (0x00000010, "PERSONAL"), (0x00000020, "CONNECTED"), (0x00000040, "SHORT_NAME"), (0x00000080, "VISIBLE"),
        (0x00000100, "SSP_SUPPORTED"), (0x00000200, "SSP_PAIRED"), (0x00000400, "SSP_MITM_PROTECTED"),
        (0x00001000, "RSSI"), (0x00002000, "EIR"), (0x00004000, "BR"), (0x00008000, "LE"),
        (0x00010000, "LE_PAIRED"), (0x00020000, "LE_PERSONAL"), (0x00040000, "LE_MITM_PROTECTED"),
        (0x00080000, "LE_PRIVACY_ENABLED"), (0x00100000, "LE_RANDOM_ADDRESS_TYPE"),
        (0x00200000, "LE_DISCOVERABLE"), (0x00400000, "LE_NAME"), (0x00800000, "LE_VISIBLE"),
        (0x01000000, "LE_CONNECTED"), (0x02000000, "LE_CONNECTABLE"),
        (0x08000000, "BR_SECURE_CONNECTION_PAIRED"), (0x10000000, "LE_SECURE_CONNECTION_PAIRED"),
        (0x20000000, "DEBUGKEY"), (0x40000000, "LE_DEBUGKEY"), (0x80000000, "TX_POWER"),
    ];

    public static void Log(string source, string message) =>
        Console.WriteLine($"{Clock.Elapsed.TotalSeconds,8:F3}s  {source,-9}  {message}");

    public static void Edge(string kind, DeviceInfo device)
    {
        if (!Live)
        {
            Interlocked.Increment(ref SnapshotEdges);
            return;
        }
        Log("periphery", $"{kind,-11} {Mask(device.Id.ToString())}  IsActive={device.IsActive}");
    }

    public static string Summary() =>
        Counts.IsEmpty ? "none" : string.Join(", ", Counts.OrderBy(c => c.Key).Select(c => $"{c.Key} x{c.Value}"));

    // CM_NOTIFY_EVENT_DATA for a device-handle filter:
    //   FilterType @0, Reserved @4, u.DeviceHandle { GUID EventGuid @8; LONG NameOffset @24;
    //   DWORD DataSize @28; BYTE Data[] @32 }
    public static unsafe void OnDeviceHandleEvent(int radio, int action, byte* data, int size)
    {
        if (action == Native.CM_NOTIFY_ACTION_DEVICEQUERYREMOVE && HeldHandles.TryRemove(radio, out var held))
        {
            Native.CloseHandle(held);
            Log("radio", $"radio {radio}: {Native.ActionName(action)}, closed the held handle");
            return;
        }
        if (action != Native.CM_NOTIFY_ACTION_DEVICECUSTOMEVENT)
        {
            Count(Native.ActionName(action));
            Log("radio", $"radio {radio}: {Native.ActionName(action)}");
            return;
        }
        if (size < 32)
        {
            Log("custom", $"radio {radio}: event data is {size} byte(s), too short for a custom event");
            return;
        }

        Guid guid = *(Guid*)(data + 8);
        int dataSize = *(int*)(data + 28);
        var payload = new ReadOnlySpan<byte>(data + 32, Math.Clamp(dataSize, 0, size - 32));

        if (guid == HciEvent && payload.Length >= 10)
        {
            // BTH_HCI_EVENT_INFO { BTH_ADDR bthAddress @0; UCHAR connectionType @8; UCHAR connected @9 }
            ulong address = BinaryPrimitives.ReadUInt64LittleEndian(payload);
            string type = payload[8] switch { 1 => "ACL", 2 => "SCO", 3 => "LE", var t => $"type {t}" };
            bool connected = payload[9] != 0;
            Count($"HCI {(connected ? "connect" : "disconnect")}");
            Log("hci", $"radio {radio}: {Alias(address)} {type} {(connected ? "CONNECTED" : "DISCONNECTED")}");
            PendingReports.Add(Task.Run(() => ReportDevnodesAsync(address, radio)));
        }
        else if (guid == L2capEvent && payload.Length >= 12)
        {
            // BTH_L2CAP_EVENT_INFO { BTH_ADDR bthAddress @0; USHORT psm @8; UCHAR connected @10; UCHAR initiated @11 }
            ulong address = BinaryPrimitives.ReadUInt64LittleEndian(payload);
            ushort psm = BinaryPrimitives.ReadUInt16LittleEndian(payload[8..]);
            bool connected = payload[10] != 0;
            string by = connected ? (payload[11] != 0 ? " by this host" : " by the remote") : "";
            Count("L2CAP");
            Log("l2cap", $"radio {radio}: {Alias(address)} psm 0x{psm:X4} {(connected ? "opened" : "closed")}{by}");
        }
        else if (guid == InRange && payload.Length >= 276)
        {
            // BTH_RADIO_IN_RANGE { BTH_DEVICE_INFO deviceInfo @0 (flags @0, address @8, cod @16,
            // name[248] @20; 272 bytes); ULONG previousDeviceFlags @272 }. The name is not printed.
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(payload);
            ulong address = BinaryPrimitives.ReadUInt64LittleEndian(payload[8..]);
            uint previous = BinaryPrimitives.ReadUInt32LittleEndian(payload[272..]);
            Count("IN_RANGE");
            Log("in-range", $"radio {radio}: {Alias(address)} {FlagDiff(previous, flags)}  now [{FlagNames(flags)}]");
        }
        else if (guid == OutOfRange && payload.Length >= 8)
        {
            Count("OUT_OF_RANGE");
            Log("out-range", $"radio {radio}: {Alias(BinaryPrimitives.ReadUInt64LittleEndian(payload))}");
        }
        else
        {
            string name = OtherEvents.TryGetValue(guid, out var known) ? known : guid.ToString();
            Count(name);
            Log("custom", $"radio {radio}: {name}, {dataSize} byte(s)");
        }
    }

    public static List<string> RadioPaths = [];
    public static uint StackAccess;

    const uint BDIF_CONNECTED = 0x00000020;
    const uint BDIF_LE_CONNECTED = 0x01000000;

    // Checks the IOCTL once before any link changes: it runs, and the addresses it parses
    // are the ones in the link devnodes' instance ids. A layout mistake shows up here as
    // zero matches rather than as a wrong answer later.
    public static async Task ReportStackAtStartupAsync()
    {
        var stack = ReadStack(out string detail);
        if (stack is null)
        {
            Log("stack", $"IOCTL_BTH_GET_DEVICE_INFO failed: {detail}");
            return;
        }

        var nodes = await Devices.Enumerate().OfCategory(DeviceCategory.Bluetooth).ToListAsync();
        int matched = stack.Keys.Count(a =>
            nodes.Any(d => d.Id.ToString().Contains($"DEV_{a:X12}", StringComparison.OrdinalIgnoreCase)));
        Log("stack", $"IOCTL_BTH_GET_DEVICE_INFO -> {stack.Count} device(s), {detail}; " +
                     $"{matched} match a link devnode; {stack.Values.Count(f => (f & BDIF_CONNECTED) != 0)} CONNECTED, " +
                     $"{stack.Values.Count(f => (f & BDIF_LE_CONNECTED) != 0)} LE_CONNECTED");
    }

    // Address -> BDIF flags, or null when no radio answered. With a radio number (1-based,
    // as printed), only that radio is asked; otherwise every radio, merged by address.
    static Dictionary<ulong, uint>? ReadStack(out string detail, int? radio = null)
    {
        Dictionary<ulong, uint>? merged = null;
        var notes = new List<string>();
        List<string> paths = radio is int r && r >= 1 && r <= RadioPaths.Count ? [RadioPaths[r - 1]] : RadioPaths;
        foreach (string path in paths)
        {
            var devices = Native.QueryStackDevices(path, StackAccess, out string note);
            notes.Add(note);
            if (devices is null) continue;
            merged ??= [];
            foreach (var (address, flags) in devices)
                merged[address] = flags;
        }
        detail = string.Join("; ", notes);
        return merged;
    }

    static async Task ReportDevnodesAsync(ulong address, int radio)
    {
        string hex = address.ToString("X12");
        foreach (int delayMs in (int[])[0, 2000])
        {
            if (delayMs > 0)
                await Task.Delay(delayMs);

            // The stack first, so at +0 ms it is read as close to the event as possible,
            // and from the radio that raised the event.
            var stack = ReadStack(out string stackDetail, radio);
            string stackLine = stack is null ? $"IOCTL failed: {stackDetail}"
                : !stack.TryGetValue(address, out uint flags) ? "address not in the stack's device list"
                : $"CONNECTED={(flags & BDIF_CONNECTED) != 0} LE_CONNECTED={(flags & BDIF_LE_CONNECTED) != 0}  [{FlagNames(flags)}]";
            Log("stack", $"{Alias(address)} +{delayMs} ms: {stackLine}");

            var nodes = await Devices.Enumerate().OfCategory(DeviceCategory.Bluetooth).ToListAsync();
            var matches = nodes.Where(d => d.Id.ToString().Contains(hex, StringComparison.OrdinalIgnoreCase)).ToList();
            string detail = matches.Count == 0
                ? "no Bluetooth devnode carries this address"
                : string.Join("; ", matches.Select(d => $"{Mask(d.Id.ToString())} IsActive={d.IsActive}"));
            Log("devnode", $"{Alias(address)} +{delayMs} ms: {detail}");
        }
    }

    static void Count(string key) => Counts.AddOrUpdate(key, 1, (_, n) => n + 1);

    static string Alias(ulong address) =>
        Aliases.GetOrAdd(address, _ => $"BT#{Interlocked.Increment(ref _nextAlias)}");

    // A 12-hex run after DEV_ is a device address and gets an alias. Any other 12-hex
    // run is aliased only if it is an address already seen, otherwise masked.
    static string Mask(string id) =>
        Regex.Replace(id, @"(?i)(DEV_)?(?<![0-9a-f])([0-9a-f]{12})(?![0-9a-f])", m =>
        {
            ulong value = Convert.ToUInt64(m.Groups[2].Value, 16);
            if (m.Groups[1].Success)
                return m.Groups[1].Value + Alias(value);
            return Aliases.TryGetValue(value, out var alias) ? alias : "<hex12>";
        });

    static string FlagNames(uint flags) =>
        string.Join("|", Bdif.Where(b => (flags & b.Bit) != 0).Select(b => b.Name));

    static string FlagDiff(uint before, uint after)
    {
        var changes = Bdif
            .Where(b => ((before ^ after) & b.Bit) != 0)
            .Select(b => ((after & b.Bit) != 0 ? "+" : "-") + b.Name)
            .ToList();
        return changes.Count == 0 ? "(no flag change)" : string.Join(" ", changes);
    }
}

static unsafe partial class Native
{
    public const int CR_SUCCESS = 0;
    const int CR_BUFFER_SMALL = 0x1A;
    const int CM_NOTIFY_FILTER_TYPE_DEVICEHANDLE = 1;
    public const int CM_NOTIFY_ACTION_DEVICEQUERYREMOVE = 2;
    public const int CM_NOTIFY_ACTION_DEVICECUSTOMEVENT = 6;
    public const uint GENERIC_READ = 0x80000000;
    public const uint FILE_READ_ATTRIBUTES = 0x80;
    public const uint FILE_SHARE_READ = 1;
    public const uint FILE_SHARE_WRITE = 2;
    public const uint OPEN_EXISTING = 3;
    public static readonly nint INVALID_HANDLE_VALUE = -1;

    // CTL_CODE(FILE_DEVICE_BLUETOOTH 0x41, 0x02, METHOD_BUFFERED, FILE_ANY_ACCESS), bthioctl.h.
    const uint IOCTL_BTH_GET_DEVICE_INFO = 0x00410008;
    const int ERROR_INSUFFICIENT_BUFFER = 122;
    const int ERROR_MORE_DATA = 234;

    // BTH_DEVICE_INFO_LIST is byte-packed: ULONG numOfDevices @0, BTH_DEVICE_INFO deviceList[] @4.
    // BTH_DEVICE_INFO keeps natural alignment: flags @0, address @8, cod @16, name[248] @20; 272 bytes.
    const int ListHeader = 4;
    const int DeviceInfoSize = 272;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(
        nint device, uint ioControlCode, void* inBuffer, uint inSize, void* outBuffer, uint outSize,
        out uint bytesReturned, nint overlapped);

    /// <summary>
    /// Every remote device the radio's stack knows, with its BDIF flags. Null on failure;
    /// <paramref name="note"/> says why, or reports the byte count so the layout can be checked.
    /// </summary>
    public static List<(ulong Address, uint Flags)>? QueryStackDevices(string radioPath, uint access, out string note)
    {
        nint radio = CreateFileW(radioPath, access, FILE_SHARE_READ | FILE_SHARE_WRITE, 0, OPEN_EXISTING, 0, 0);
        if (radio == INVALID_HANDLE_VALUE)
        {
            note = $"CreateFile error {Marshal.GetLastPInvokeError()}";
            return null;
        }

        try
        {
            int capacity = 64;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                int size = ListHeader + capacity * DeviceInfoSize;
                var buffer = new byte[size];
                BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)capacity);

                bool ok;
                uint returned;
                fixed (byte* p = buffer)
                    ok = DeviceIoControl(radio, IOCTL_BTH_GET_DEVICE_INFO, p, (uint)size, p, (uint)size, out returned, 0);

                if (!ok)
                {
                    int error = Marshal.GetLastPInvokeError();
                    if (error is ERROR_MORE_DATA or ERROR_INSUFFICIENT_BUFFER)
                    {
                        capacity *= 4;
                        continue;
                    }
                    note = $"DeviceIoControl error {error}";
                    return null;
                }

                int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer);
                if (count > capacity)
                {
                    capacity = count;
                    continue;
                }

                int expected = ListHeader + count * DeviceInfoSize;
                if (returned < expected)
                {
                    note = $"{returned} bytes returned for {count} device(s), fewer than the {expected} the layout needs";
                    return null;
                }

                note = $"{returned} bytes returned for {count} device(s), layout predicts {expected}";
                var devices = new List<(ulong, uint)>(count);
                for (int i = 0; i < count; i++)
                {
                    var entry = buffer.AsSpan(ListHeader + i * DeviceInfoSize, DeviceInfoSize);
                    devices.Add((BinaryPrimitives.ReadUInt64LittleEndian(entry[8..]), BinaryPrimitives.ReadUInt32LittleEndian(entry)));
                }
                return devices;
            }

            note = "device list kept growing";
            return null;
        }
        finally
        {
            CloseHandle(radio);
        }
    }

    static readonly Guid GUID_BTHPORT_DEVICE_INTERFACE = new("0850302a-b344-4fda-9be9-90576b8d46f0");

    static readonly string[] ActionNames =
    [
        "DEVICEINTERFACEARRIVAL", "DEVICEINTERFACEREMOVAL", "DEVICEQUERYREMOVE", "DEVICEQUERYREMOVEFAILED",
        "DEVICEREMOVEPENDING", "DEVICEREMOVECOMPLETE", "DEVICECUSTOMEVENT", "DEVICEINSTANCEENUMERATED",
        "DEVICEINSTANCESTARTED", "DEVICEINSTANCEREMOVED",
    ];

    public static string ActionName(int action) =>
        action >= 0 && action < ActionNames.Length ? ActionNames[action] : $"action {action}";

    // 416 bytes: the union's largest member is DeviceInstance.InstanceId[MAX_DEVICE_ID_LEN].
    [StructLayout(LayoutKind.Explicit, Size = 416)]
    struct CM_NOTIFY_FILTER
    {
        [FieldOffset(0)]  public int cbSize;
        [FieldOffset(4)]  public int Flags;
        [FieldOffset(8)]  public int FilterType;
        [FieldOffset(12)] public int Reserved;
        [FieldOffset(16)] public nint hTarget;
    }

    [LibraryImport("cfgmgr32.dll")]
    private static partial int CM_Register_Notification(
        ref CM_NOTIFY_FILTER pFilter,
        nint pContext,
        delegate* unmanaged[Stdcall]<nint, nint, int, nint, int, int> pCallback,
        out nint pNotifyContext);

    [LibraryImport("cfgmgr32.dll")]
    public static partial int CM_Unregister_Notification(nint notifyContext);

    [LibraryImport("cfgmgr32.dll")]
    private static partial int CM_Get_Device_Interface_List_SizeW(out uint length, ref Guid interfaceClass, char* deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial int CM_Get_Device_Interface_ListW(ref Guid interfaceClass, char* deviceId, char* buffer, uint length, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateFileW(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    public static List<string> RadioInterfacePaths()
    {
        var interfaceClass = GUID_BTHPORT_DEVICE_INTERFACE;
        // The list can grow between the size query and the read.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (CM_Get_Device_Interface_List_SizeW(out uint length, ref interfaceClass, null, 0) != CR_SUCCESS)
                return [];
            var buffer = new char[length];
            int cr;
            fixed (char* p = buffer)
                cr = CM_Get_Device_Interface_ListW(ref interfaceClass, null, p, length, 0);
            if (cr == CR_BUFFER_SMALL)
                continue;
            if (cr != CR_SUCCESS)
                return [];
            return [.. new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries)];
        }
        return [];
    }

    public static int RegisterForDeviceHandleEvents(nint handle, int radio, out nint notify)
    {
        var filter = new CM_NOTIFY_FILTER
        {
            cbSize = sizeof(CM_NOTIFY_FILTER),
            FilterType = CM_NOTIFY_FILTER_TYPE_DEVICEHANDLE,
            hTarget = handle,
        };
        return CM_Register_Notification(ref filter, radio, &OnNotify, out notify);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static int OnNotify(nint hNotify, nint context, int action, nint eventData, int eventDataSize)
    {
        try
        {
            Probe.OnDeviceHandleEvent((int)context, action, (byte*)eventData, eventDataSize);
        }
        catch (Exception ex)
        {
            Probe.Log("probe", $"callback threw {ex.GetType().Name}: {ex.Message}");
        }
        return 0; // ERROR_SUCCESS. Never veto a query remove.
    }
}

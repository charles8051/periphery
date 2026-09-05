// Reads a Treehopper board's identity records straight out of its USB string descriptors and
// reconstructs the bytes sitting in the config page behind them. For issue #170 / ADR-0086 D5
// test 4: capturing what the desync actually wrote, on the damaged boards, before anything
// reflashes them and destroys the evidence.
//
//   dotnet run --project scratch/TreehopperIdentityProbe            # every board
//   dotnet run --project scratch/TreehopperIdentityProbe -- --json  # machine-readable
//
// READ-ONLY. Control transfers with bmRequestType 0x80 - device-to-host, standard, device.
// It opens no endpoint, writes nothing, and never enters the bootloader. Safe on a running
// kiosk board, which is the point: the damaged boards must not be reflashed before this runs.
//
// WHY THIS WORKS WITHOUT C2
//
// The desync did not scribble over the config page at random - it called writeUsbString with a
// length and a payload, which wrote a WELL-FORMED record: marker, length, descriptor type,
// then nine bytes of pixel data. So the USB stack serves it like any other string, and the
// descriptor gives back everything the record holds:
//
//   flash:      [0]=0x01 marker   [1]=(len+1)*2   [2]=0x03   [3..] = len packed bytes
//   descriptor:                   bLength         bDescType  widened to UTF-16LE, 2 bytes each
//
// bLength IS the stored length byte, and every other descriptor byte is a stored payload byte.
// The EFM8 factory bootloader has no read command at all (Identify/Setup/Erase/Write/Verify/
// Lock/RunApp), so short of C2 this is the only way to read the page - and for a well-formed
// record it recovers all of it except byte [0], which must be the packed marker or the stack
// would not have served it this way.
//
// WHAT C2 WOULD STILL ADD: the bytes OUTSIDE the two 64-byte records (0xF880-0xFBBF), and byte
// [0] directly. On the damaged boards the derived write had len = 9 - short - so nothing
// outside the records should have been touched. Worth confirming if a probe is to hand; not
// worth gating a site visit on.
//
// WHY NOT READ THE SERIAL OFF THE OS
//
// Because the OS lies about its case. On Windows the same board appears as `cDYhINBh` through
// the device-notification path and `CDYHINBH` through SetupAPI, simultaneously - which is the
// entire "case flips" symptom in #170, and it is host-side normalisation, not flash. The
// string descriptor is the device's own bytes. See ADR-0086 D5 test 3.

using System.Globalization;
using System.Text;
using Periphery.Usb;

const byte DescriptorTypeString = 0x03;
const ushort LangIdEnglishUs = 0x0409;

// Descriptor indices, from the firmware's own device descriptor (descriptors.c).
const byte IManufacturer = 1;
const byte IProduct      = 2;   // the NAME record at NAME_ADDR 0xF840
const byte ISerialNumber = 3;   // the SERIAL record at SER_ADDR 0xF800

const string BackslashEscape = @"\\";
const string QuoteEscape     = "\\\"";
const string NewlineEscape   = @"\n";
const string ReturnEscape    = @"\r";
const string TabEscape       = @"\t";
const string BackspaceEscape = @"\b";
const string FormFeedEscape  = @"\f";
const string UnicodeEscape   = @"\u";

bool json = args.Contains("--json");

var boards = await TreehopperBoard.EnumerateAsync();
if (boards.Count == 0)
{
    Console.Error.WriteLine("No Treehopper board (10C4:8A7E) is connected.");
    return 1;
}

var results = new List<string>();
int failures = 0;

foreach (var board in boards)
{
    Console.WriteLine(json ? "" : $"── {board.Id}");
    try
    {
        await using var usb = await UsbDevice.OpenAsync(board, default, TimeSpan.FromSeconds(3), null);

        var langs = await StringDescriptorAsync(usb, 0, 0);
        ushort lang = langs.Length >= 4 ? (ushort)(langs[2] | (langs[3] << 8)) : LangIdEnglishUs;

        var records = new List<(string Which, byte Index, byte[] Raw)>();
        foreach (var (which, idx) in new[]
                 { ("serial (0xF800)", ISerialNumber), ("name   (0xF840)", IProduct), ("mfr", IManufacturer) })
            records.Add((which, idx, await StringDescriptorAsync(usb, idx, lang)));

        foreach (var (which, idx, raw) in records)
        {
            if (json) { results.Add(JsonFor(board.Id, which, idx, raw)); continue; }
            Report(which, idx, raw);
        }
    }
    catch (Exception ex)
    {
        // A failed read must never leave a clean-looking result behind. This probe exists to
        // produce evidence about damaged boards, and a board too damaged to answer is the most
        // interesting case there is - it must not be silently absent from a JSON array that
        // downstream analysis then reads as "these are the boards".
        failures++;
        Console.Error.WriteLine($"  FAILED to read {board.Id}: {ex.Message}");
        if (json) results.Add(ErrorFor(board.Id, ex.Message));
        else Console.Error.WriteLine(
            "  A board whose descriptors cannot be read at all is itself a finding - record it, "
            + "and do not reflash before someone has looked.");
    }
    if (!json) Console.WriteLine();
}

if (json) Console.WriteLine("[\n  " + string.Join(",\n  ", results) + "\n]");
if (failures > 0)
    Console.Error.WriteLine(
        $"{failures} of {boards.Count} board(s) could not be read. Exit status reflects that; do "
        + "not treat this run as a complete picture.");
return failures > 0 ? 1 : 0;

// ── Reading ──────────────────────────────────────────────────────────────────

static async Task<byte[]> StringDescriptorAsync(UsbDevice usb, byte index, ushort lang)
{
    // Two-step, the way a host stack does it: read the first two bytes for bLength, then the
    // whole thing. Asking for 255 up front works on most devices but a short-packet-terminated
    // reply can hide a truncation, and a truncated read is exactly the kind of thing that would
    // be mistaken for damage here.
    var head = new byte[2];
    int n = await usb.ControlTransferAsync(Setup(index, lang, 2), head);
    if (n < 2 || head[0] < 2) return head[..Math.Max(n, 0)];

    var full = new byte[head[0]];
    n = await usb.ControlTransferAsync(Setup(index, lang, head[0]), full);
    return full[..Math.Max(n, 0)];

    static UsbControlSetup Setup(byte index, ushort lang, int len) => new()
    {
        RequestType = 0x80,                                       // device-to-host, standard, device
        Request     = 0x06,                                       // GET_DESCRIPTOR
        Value       = (ushort)((DescriptorTypeString << 8) | index),
        Index       = lang,
        // wLength is carried by the buffer size in this API; `len` is here for the caller's clarity.
    };
}

// ── Reporting ────────────────────────────────────────────────────────────────

static void Report(string which, byte index, byte[] raw)
{
    Console.WriteLine($"  {which}  (string index {index})");
    if (raw.Length < 2)
    {
        Console.WriteLine($"    EMPTY / unreadable - {raw.Length} bytes returned.");
        return;
    }

    Console.WriteLine($"    descriptor : {Hex(raw)}");
    Console.WriteLine($"    bLength    : 0x{raw[0]:X2} ({raw[0]})   bDescriptorType: 0x{raw[1]:X2}");

    // bLength is the device's own claim about the record; a short transfer means it did not
    // deliver what it advertised. Reconstructing anyway would turn a transfer problem into
    // what reads as a shortened identity - which on these boards is the very thing under
    // investigation, so it must not be manufactured here.
    if (raw.Length != raw[0])
    {
        Console.WriteLine(
            $"    TRUNCATED  : bLength says {raw[0]} bytes, {raw.Length} arrived. The "
            + "reconstruction below is NOT the record - fix the transfer and re-read before "
            + "drawing any conclusion from it.");
        return;
    }

    // The packed bytes are every other descriptor byte after the 2-byte header. This is the
    // widening in reverse, and it is what is physically in the flash page.
    var packed = new byte[Math.Max(0, (raw.Length - 2) / 2)];
    for (int i = 0; i < packed.Length; i++) packed[i] = raw[2 + i * 2];

    // The high byte of each widened char should be zero for a packed record. Anything else means
    // the record is not what this tool assumes, and the raw descriptor above is the evidence.
    bool packedShape = true;
    for (int i = 0; i < packed.Length && packedShape; i++) packedShape = raw[3 + i * 2] == 0x00;

    Console.WriteLine($"    flash [1]  : 0x{raw[0]:X2}  -> payload length {(raw[0] - 2) / 2}");
    Console.WriteLine($"    flash [3..]: {Hex(packed)}");
    Console.WriteLine($"    as text    : {Printable(packed)}");
    if (!packedShape)
        Console.WriteLine("    NOTE: a widened high byte was non-zero - this is not a packed record. "
                          + "Trust the raw descriptor line, not the reconstruction.");

    if (Period4(packed) is { } group)
        Console.WriteLine($"    *** PERIOD-4 REPEAT of {Hex(group)} - this is APA102 pixel data "
                          + $"executed as a command (#170). Opcode byte would be 0x{group[0]:X2}.");
}

// The signature of the #170 damage: the payload is a repeating 4-byte group, because it is a run
// of APA102 pixels [header, B, G, R] entered at some phase. Reported, never assumed - a short
// payload can repeat by chance, so this needs at least two full groups to say anything.
static byte[]? Period4(byte[] b)
{
    if (b.Length < 8) return null;
    for (int i = 4; i < b.Length; i++)
        if (b[i] != b[i - 4]) return null;
    return b[..4];
}

static string Hex(byte[] b) => b.Length == 0 ? "(none)" : Convert.ToHexString(b).Chunk(2)
    .Select(c => new string(c)).Aggregate((a, c) => a + " " + c);

static string Printable(byte[] b)
{
    var sb = new StringBuilder("\"");
    foreach (var c in b) sb.Append(c is >= 0x20 and < 0x7F ? (char)c : '.');
    return sb.Append('"').ToString();
}

static string ErrorFor(string id, string message) =>
    "{"
    + $"\"device\":\"{Esc(id)}\","
    + "\"error\":true,"
    + $"\"message\":\"{Esc(message)}\""
    + "}";

// Full JSON string escaping, not just quote-and-backslash. An exception message is arbitrary
// text - a newline or a tab in it produces a document that will not parse, and the caller
// most likely to hit that is the one reading a FAILED board, which is the row that matters
// most. Control characters below 0x20 have no literal form in JSON and must be escaped.
static string Esc(string s)
{
    var sb = new StringBuilder(s.Length + 8);
    foreach (char c in s)
    {
        // Compared by code point rather than by character literal, so neither this source nor
        // anything that rewrites it has to survive a second round of escaping.
        switch ((int)c)
        {
            case 0x5C: sb.Append(BackslashEscape); break;
            case 0x22: sb.Append(QuoteEscape); break;
            case 0x0A: sb.Append(NewlineEscape); break;
            case 0x0D: sb.Append(ReturnEscape); break;
            case 0x09: sb.Append(TabEscape); break;
            case 0x08: sb.Append(BackspaceEscape); break;
            case 0x0C: sb.Append(FormFeedEscape); break;
            default:
                if (c < 0x20)
                    sb.Append(UnicodeEscape).Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else
                    sb.Append(c);
                break;
        }
    }
    return sb.ToString();
}

static string JsonFor(string id, string which, byte index, byte[] raw)
{
    var packed = new byte[Math.Max(0, (raw.Length - 2) / 2)];
    for (int i = 0; i < packed.Length; i++) packed[i] = raw[2 + i * 2];
    bool truncated = raw.Length < 2 || raw.Length != raw[0];
    return "{"
        + $"\"device\":\"{Esc(id)}\","
        + $"\"record\":\"{Esc(which.Trim())}\","
        + $"\"stringIndex\":{index},"
        + $"\"descriptor\":\"{Convert.ToHexString(raw)}\","
        + $"\"flashLengthByte\":{(raw.Length >= 1 ? raw[0] : 0)},"
        + $"\"bytesReturned\":{raw.Length},"
        + $"\"truncated\":{(truncated ? "true" : "false")},"
        + $"\"packed\":\"{(truncated ? "" : Convert.ToHexString(packed))}\""
        + "}";
}

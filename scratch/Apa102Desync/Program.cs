// Bench harness for issue #170 / ADR-0086 D5: reproduce the EP_PeripheralConfig desync
// directly, on a real board, and tell the operator whether a pixel byte was executed as a
// command.
//
//   dotnet run --project scratch/Apa102Desync -- \
//       [--iterations 200] [--stall-ms 50] [--canary 0x01] [--serial <sn>] [--list]
//
// WHAT IT DOES
//
// It drives the exact traffic that destroyed two production boards: a 63-pixel APA102 frame,
// chunked at 252 bytes the way Apa102Strip.FlushAsync chunks it, giving a 259-byte
// SPITransaction command - USB packets of 64/64/64/64/3. It then STALLS THE HOST after the
// first packet, which is what pushes the firmware's continuation read past its spin budget
// and onto the USBD_AbortTransfer path. On firmware without the #170 fix that abort discards
// in-flight packets, the endpoint is re-armed at offset 0, and the next surviving packet -
// pure pixel data - lands where an opcode is expected.
//
// WHY THE PHASE IS ALWAYS BLUE. The encoded stream is 4 start bytes then [header B G R] per
// pixel. A packet boundary at command offset 64k is stream offset 64k-7, and
// (64k - 7 - 4) mod 4 == 1 for every k - index 1 of the group, the Blue channel. That is
// exactly the phase the field evidence showed, and it is not a coincidence: 64 mod 4 == 0,
// so every packet after the first starts on the same channel.
//
// THE CANARY IS DELIBERATELY THE SAFEST OPCODE THERE IS
//
// The pixel is Rgb(R:0x00, G:0x00, B:--canary), so the wire group is [0xFF, canary, 0x00,
// 0x00]. Default canary 0x01 is ConfigureDevice, which calls Treehopper_Init() and touches
// no flash. Every OTHER phase of that group is inert: 0xFF is not an opcode and neither is
// 0x00. So a desync during this run resets the board's pin configuration and nothing else.
//
// It would be trivial to point the canary at 0x0B FirmwareUpdateName and watch the board
// destroy its own name page. DON'T - and the harness refuses to. That is the damage the
// issue is about, it is not reversible from the host for the serial, and the mechanism is
// already proven by a 0x01 that fires.
//
// HOW IT KNOWS
//
// Pin 0 is configured as a digital input, so the board's pin-report stream on EP1 IN stops
// reporting it as "reserved". Treehopper_Init() puts every pin back to ReservedPin, which
// the firmware reports as 0xFF/0xFF (treehopper.c SendPinStatus, `default:`). A report where
// all 20 pins read 0xFFFF is therefore a ConfigureDevice the host never sent - i.e. a pixel
// byte executed as a command. Host-observable; no analyser needed for the primary result.
//
// POSITIVE CONTROL. Before the real run the harness sends one honest ConfigureDevice and
// requires the detector to fire. A run that skips that check cannot tell "no desync" from
// "detector broken", and that is exactly the shape of bench test that passes having tested
// nothing. If the control does not fire, the harness exits non-zero and runs nothing.
//
// FOR THE ANALYSER. Put the probe on EP2 OUT. The harness prints the repeating pixel group
// and the packet split, so the trace lines up against what the host believed it sent. Look
// for a packet that begins with the canary byte and is not a command the host issued.
//
// WHY THIS SPEAKS THE WIRE PROTOCOL DIRECTLY
//
// TreehopperWire, Command and Apa102Encoder are all internal to their assemblies, and this
// harness does not reach past that. It builds the packets from the firmware's own contract
// (inc/treehopper.h GlobalCommands_t, treehopper.c ProcessPeripheralConfigPacket) instead,
// which is the right dependency for a reproduction: the claim under test is about what the
// firmware does with bytes on the wire, not about what our codec believes it emitted. The
// one coupling that matters - that the shipped Apa102Encoder really does lay pixels out in
// the period-4 groups assumed here - is pinned by
// Apa102EncoderTests.Encode_GroupLayout_IsWhatTheDesyncHarnessAssumes.

using System.Globalization;
using Periphery.Usb;

// ── Wire protocol (firmware: inc/treehopper.h, treehopper.c) ──────────────────

const byte EpPinConfig        = 0x01;   // OUT
const byte EpPeripheralConfig = 0x02;   // OUT - the endpoint #170 is about
const byte EpPinReport        = 0x81;   // IN
const int  MaxPacket          = 64;

const byte CmdConfigureDevice      = 0x01;
const byte CmdSpiConfig            = 0x05;
const byte CmdSpiTransaction       = 0x07;
const byte CmdFirmwareUpdateSerial = 0x0A;
const byte CmdFirmwareUpdateName   = 0x0B;
const byte CmdReboot               = 0x0C;
const byte CmdEnterBootloader      = 0x0D;

const byte PinCmdDigitalInput = 1;
const byte SpiBurstTx         = 1;
const byte SpiMode11          = 0x30;   // CPOL=1, CPHA=1 - what an APA102 needs
const int  PinCount           = 20;
const int  PinReportLength    = 1 + PinCount * 2;

// ── Arguments ────────────────────────────────────────────────────────────────

int iterations = 200;
int stallMs = 50;
byte canary = CmdConfigureDevice;
string? wantSerial = null;
bool listOnly = false;

for (int i = 0; i < args.Length; i++)
{
    string Next() => ++i < args.Length ? args[i] : throw new ArgumentException($"missing value for {args[i - 1]}");
    switch (args[i])
    {
        case "--iterations": iterations = int.Parse(Next(), CultureInfo.InvariantCulture); break;
        case "--stall-ms":   stallMs = int.Parse(Next(), CultureInfo.InvariantCulture); break;
        case "--canary":     canary = ParseByte(Next()); break;
        case "--serial":     wantSerial = Next(); break;
        case "--list":       listOnly = true; break;
        default: throw new ArgumentException($"unknown argument '{args[i]}'");
    }
}

// Refusing these is the difference between a harness and a weapon. The mechanism is proven
// by any opcode that fires, and these four are the ones that end boards.
if (canary is CmdFirmwareUpdateSerial or CmdFirmwareUpdateName or CmdReboot or CmdEnterBootloader)
{
    Console.Error.WriteLine(
        $"--canary 0x{canary:X2} is a destructive opcode (serial write, name write, reboot, or "
        + "bootloader entry). This harness will not aim pixel data at it. The default 0x01 "
        + "ConfigureDevice proves the same mechanism without touching flash.");
    return 2;
}

var boards = await TreehopperBoard.EnumerateAsync();
if (boards.Count == 0) { Console.Error.WriteLine("No Treehopper board is connected."); return 1; }

if (listOnly)
{
    foreach (var b in boards) Console.WriteLine($"  {b.SerialNumber ?? "?",-12}  {b.Name}");
    return 0;
}

var target = wantSerial is null
    ? boards[0]
    : boards.FirstOrDefault(b => string.Equals(b.SerialNumber, wantSerial, StringComparison.OrdinalIgnoreCase));
if (target is null) { Console.Error.WriteLine($"No board with serial '{wantSerial}'."); return 1; }
if (boards.Count > 1 && wantSerial is null)
    Console.WriteLine($"NOTE: {boards.Count} boards connected; using the first. Pass --serial to choose.");

// Recorded now so the summary can show the run left the config page alone. These come from
// the descriptors, which is where the field damage showed up.
string nameBefore   = target.Name ?? "";
string serialBefore = target.SerialNumber ?? "";

Console.WriteLine($"Board   : '{nameBefore}' serial '{serialBefore}'");
Console.WriteLine($"Canary  : 0x{canary:X2} in the Blue channel -> pixel Rgb(00,00,{canary:X2})");
Console.WriteLine($"Stall   : {stallMs} ms after packet 0 of each 259-byte command");
Console.WriteLine($"Run     : {iterations} iterations. Ctrl-C to stop early.");
Console.WriteLine();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
var ct = cts.Token;

// Raw USB, not TreehopperBoard: the point is to control packet timing INSIDE one command,
// and the board's WriteChunkedAsync ships all five packets back to back.
await using var usb = await UsbDevice.OpenAsync(target, ct, TimeSpan.FromSeconds(2), logger: null);

// ── Detector ─────────────────────────────────────────────────────────────────

int resetFlag = 0;
long reports = 0;

var reader = Task.Run(async () =>
{
    try
    {
        while (!ct.IsCancellationRequested)
        {
            var raw = await usb.BulkReadAsync(EpPinReport, MaxPacket, ct);
            if (raw.Length < PinReportLength) continue;
            Interlocked.Increment(ref reports);

            bool allReserved = true;
            for (int p = 0; p < PinCount && allReserved; p++)
                allReserved = raw[1 + p * 2] == 0xFF && raw[2 + p * 2] == 0xFF;
            if (allReserved)
                Interlocked.Exchange(ref resetFlag, 1);
        }
    }
    catch (OperationCanceledException) { }
    catch (UsbException) { /* link gone; the summary says so */ }
}, ct);

async Task WriteCommandAsync(byte endpoint, byte[] bytes)
{
    for (int off = 0; off < bytes.Length; off += MaxPacket)
        await usb.BulkWriteAsync(endpoint, bytes.AsMemory(off, Math.Min(MaxPacket, bytes.Length - off)), ct);
}

// Arms the detector: pin 0 stops reading as reserved, so the next all-reserved report is an
// event rather than the board's resting state.
async Task ArmDetectorAsync()
{
    Interlocked.Exchange(ref resetFlag, 0);
    await WriteCommandAsync(EpPinConfig, [0, PinCmdDigitalInput, 0, 0, 0, 0]);
    await Task.Delay(200, ct);
    Interlocked.Exchange(ref resetFlag, 0);
}

bool Fired() => Interlocked.CompareExchange(ref resetFlag, 0, 0) == 1;

async Task<bool> WaitForResetAsync(TimeSpan within)
{
    var deadline = DateTime.UtcNow + within;
    while (DateTime.UtcNow < deadline)
    {
        if (Fired()) return true;
        await Task.Delay(25, ct);
    }
    return false;
}

// ── Positive control ─────────────────────────────────────────────────────────

Console.WriteLine("Positive control: does the detector see a board reset the host DID ask for?");
await WriteCommandAsync(EpPeripheralConfig, [CmdConfigureDevice, 0x00]);
await Task.Delay(300, ct);
await ArmDetectorAsync();
await WriteCommandAsync(EpPeripheralConfig, [CmdConfigureDevice, 0x00]);

if (!await WaitForResetAsync(TimeSpan.FromSeconds(3)))
{
    Console.Error.WriteLine(
        $"POSITIVE CONTROL FAILED. A ConfigureDevice the host DID send did not show up as an "
        + $"all-pins-reserved report ({Interlocked.Read(ref reports)} reports seen). The detector "
        + "cannot tell a desync from silence, so this run would prove nothing. Not running.");
    await cts.CancelAsync();
    try { await reader; } catch { }
    return 3;
}
Console.WriteLine("Positive control OK.");
Console.WriteLine();

// ── The frame ────────────────────────────────────────────────────────────────

// 63 pixels: 4 start + 63*4 + ceil(63/16)=4 end = 260 encoded bytes, so Apa102Strip's
// 252-byte chunking gives a first chunk of exactly 252 -> a 259-byte command ->
// 64/64/64/64/3. That is the shape that takes the firmware's multi-packet path on every
// animation tick. Layout mirrors Apa102Encoder and is pinned by its tests.
const int LedCount = 63;
const int ChunkBytes = 252;
const byte Brightness = 31;   // header 0xE0 | 31 == 0xFF, the brightest header there is

var stream = new byte[4 + LedCount * 4 + (LedCount + 15) / 16];
for (int px = 0, pos = 4; px < LedCount; px++)
{
    stream[pos++] = (byte)(0xE0 | Brightness);
    stream[pos++] = canary;   // Blue  - the channel every packet boundary lands on
    stream[pos++] = 0x00;     // Green - would be the length byte; inert
    stream[pos++] = 0x00;     // Red   - inert
}

byte[] SpiTransaction(ReadOnlySpan<byte> tx)
{
    var packet = new byte[7 + tx.Length];
    packet[0] = CmdSpiTransaction;
    packet[1] = 0xFF;             // no chip select
    packet[2] = 0x00;             // CS mode
    packet[3] = 3;                // clock: round(24/6 - 1) == 3, i.e. 6 MHz
    packet[4] = SpiMode11;
    packet[5] = SpiBurstTx;       // transmit-only: the strip sends nothing back
    packet[6] = (byte)tx.Length;
    tx.CopyTo(packet.AsSpan(7));
    return packet;
}

var head = SpiTransaction(stream.AsSpan(0, ChunkBytes));
var tail = SpiTransaction(stream.AsSpan(ChunkBytes));
int packets = (head.Length + MaxPacket - 1) / MaxPacket;

Console.WriteLine(
    $"Frame   : {LedCount} px -> {stream.Length} encoded bytes -> a {head.Length}-byte command in "
    + $"{packets} packets, then a {tail.Length}-byte tail.");
Console.WriteLine($"Group   : {Convert.ToHexString(stream.AsSpan(4, 4))} repeating (header, B, G, R)");
Console.WriteLine(
    $"Analyser: on EP2 OUT, look for a packet starting 0x{canary:X2} that the host never sent as a "
    + "command. Packets 1..n all begin on the Blue channel.");
Console.WriteLine();

await WriteCommandAsync(EpPeripheralConfig, [CmdSpiConfig, 0x01]);
await ArmDetectorAsync();

// ── The run ──────────────────────────────────────────────────────────────────

int desyncs = 0;
int done = 0;
try
{
    for (; done < iterations && !ct.IsCancellationRequested; done++)
    {
        // Packet 0, then the stall. By now the firmware has read the 7-byte header, seen a
        // 252-byte transaction, armed the continuation read at &Treehopper_PeripheralConfig[64]
        // and started spinning on it. The stall is what runs that spin out.
        await usb.BulkWriteAsync(EpPeripheralConfig, head.AsMemory(0, MaxPacket), ct);
        await Task.Delay(stallMs, ct);

        for (int off = MaxPacket; off < head.Length; off += MaxPacket)
            await usb.BulkWriteAsync(EpPeripheralConfig, head.AsMemory(off, Math.Min(MaxPacket, head.Length - off)), ct);

        await WriteCommandAsync(EpPeripheralConfig, tail);
        await Task.Delay(30, ct);

        if (Fired())
        {
            desyncs++;
            Console.WriteLine(
                $"  iteration {done + 1,4}: DESYNC - the board reset itself. A 0x{canary:X2} pixel "
                + "byte was executed as an opcode.");
            // Put the board back where the next iteration can detect the next one.
            await WriteCommandAsync(EpPeripheralConfig, [CmdSpiConfig, 0x01]);
            await ArmDetectorAsync();
        }
    }
}
catch (OperationCanceledException) { }
catch (UsbException ex) { Console.Error.WriteLine($"USB transfer failed after {done} iterations: {ex.Message}"); }

await cts.CancelAsync();
try { await reader; } catch { /* cancelled */ }

// ── Summary ──────────────────────────────────────────────────────────────────

Console.WriteLine();
Console.WriteLine($"Iterations : {done}");
Console.WriteLine($"Desyncs    : {desyncs}");
Console.WriteLine($"Reports    : {Interlocked.Read(ref reports)}");

// Re-enumerate rather than trusting the DeviceInfo we opened with: the descriptors are the
// thing #170 destroyed, and a fresh read is the only honest check.
var after = (await TreehopperBoard.EnumerateAsync())
    .FirstOrDefault(b => b.SerialNumber == serialBefore || b.Name == nameBefore);
if (after is null)
{
    Console.Error.WriteLine(
        "The board did not re-enumerate under its old name OR serial. Check whether it is now on "
        + "VID_10C4&PID_EAC9 (the EFM8 bootloader) before assuming it is gone.");
}
else if (after.Name == nameBefore && after.SerialNumber == serialBefore)
{
    Console.WriteLine($"Descriptors: unchanged ('{after.Name}' / '{after.SerialNumber}').");
}
else
{
    Console.Error.WriteLine(
        $"Descriptors: CHANGED. name '{nameBefore}' -> '{after.Name}', serial '{serialBefore}' -> "
        + $"'{after.SerialNumber}'. Capture the config page over C2 before reflashing (#170 bench "
        + "test 4).");
}

Console.WriteLine();
Console.WriteLine(
    desyncs > 0
        ? "RESULT: reproduced. This firmware executes pixel data as commands on the abort path."
        : $"RESULT: no desync in {done} iterations. That is the expected result on firmware with "
          + "the #170 fix. On UNFIXED firmware it means the stall did not run the spin out - raise "
          + "--stall-ms and re-run before concluding anything.");

return desyncs > 0 ? 1 : 0;

static byte ParseByte(string s)
    => s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? byte.Parse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
        : byte.Parse(s, CultureInfo.InvariantCulture);

// Deterministic SPI signal generator for a Saleae baseline capture.
// Emits a known, repeating byte pattern as transmit-only (BurstTx) SPI bursts at
// a fixed clock, with CS framing each burst. Wire: SCK -> Saleae ch0,
// MOSI -> Saleae ch1 (CS optional on ch2).
//
//   dotnet run --project scratch/SpiEmit -- \
//       [--clock-mhz 1] [--bytes 8] [--cs-pin 0] [--duration 5] [--gap-ms 5]
//
// Pattern is 0x01,0x02,0x04,...,0x80,0xAA,0x55,... (length = --bytes), repeated
// every --gap-ms until --duration elapses (or Ctrl-C). A walking-1 + 0xAA/0x55
// pattern makes every bit edge unambiguous on the decode.

using System.Globalization;

// NB: on this firmware the SPI signals are on P0.0=SCK, P0.1=MISO, P0.2=MOSI
// (spi.c SCK_BIT/MISO_BIT/MOSI_BIT) == Treehopper pins 0/1/2. So the CS pin must
// NOT be 0/1/2 or it collides with the clock/data lines. Default to no CS (-1);
// pass --cs-pin 5 (or any pin >=3) for hardware CS framing.
double clockMhz = 1.0;
int bytes = 8;
int csPin = -1;
double durationSec = 5;
int gapMs = 5;

for (int i = 0; i < args.Length; i++)
{
    string Next() => ++i < args.Length ? args[i] : throw new ArgumentException($"missing value for {args[i - 1]}");
    switch (args[i])
    {
        case "--clock-mhz": clockMhz = double.Parse(Next(), CultureInfo.InvariantCulture); break;
        case "--bytes":     bytes = int.Parse(Next(), CultureInfo.InvariantCulture); break;
        case "--cs-pin":    csPin = int.Parse(Next(), CultureInfo.InvariantCulture); break;
        case "--duration":  durationSec = double.Parse(Next(), CultureInfo.InvariantCulture); break;
        case "--gap-ms":    gapMs = int.Parse(Next(), CultureInfo.InvariantCulture); break;
        default: throw new ArgumentException($"unknown argument '{args[i]}'");
    }
}

// Known pattern: walking 1s then 0xAA/0x55 alternation, trimmed/extended to --bytes.
var full = new byte[] { 0x01, 0x02, 0x04, 0x08, 0x10, 0x20, 0x40, 0x80, 0xAA, 0x55, 0xF0, 0x0F };
var tx = new byte[bytes];
for (int i = 0; i < bytes; i++) tx[i] = full[i % full.Length];

Console.WriteLine($"SPI emit: {bytes} bytes @ {clockMhz:0.###} MHz, CS pin {csPin}, " +
                  $"gap {gapMs} ms, for {durationSec:0.#}s. Pattern: {Convert.ToHexString(tx)}");
Console.WriteLine("Wire SCK->ch0, MOSI->ch1. Ctrl-C to stop early.");

TreehopperBoard board;
try { board = await TreehopperBoard.OpenFirstAsync(); }
catch (TreehopperException ex) { Console.Error.WriteLine(ex.Message); return 1; }

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(durationSec));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
var ct = cts.Token;

long bursts = 0;
await using (board)
{
    Console.WriteLine($"Connected to '{board.DeviceInfo.Name}' (fw {board.VersionString}).");
    await using var spi = await board.UseSpiAsync(clockMhz: clockMhz, ct: ct);
    if (csPin >= 0)
        await board.Pins[csPin].ConfigureAsync(PinMode.PushPullOutput, ct);

    try
    {
        while (!ct.IsCancellationRequested)
        {
            await spi.WriteAsync(tx, chipSelectPin: csPin, chipSelectMode: ChipSelectMode.SpiActiveLow, ct: ct);
            bursts++;
            if (gapMs > 0) await Task.Delay(gapMs, ct);
        }
    }
    catch (OperationCanceledException) { /* done */ }
}

Console.WriteLine($"Emitted {bursts} SPI bursts.");
return 0;

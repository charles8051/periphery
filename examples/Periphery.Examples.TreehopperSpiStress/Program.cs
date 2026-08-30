// Treehopper EFM8 SPI / USB lock-up — reproduction harness
// ========================================================
// Goal: provoke the field-reported wedge where a USB packet arriving while an
// SPI transaction is mid-flight locks up the board (it stops responding on USB
// until re-plug). See docs/explorations/treehopper-spi-usb-lockup.md.
//
// Strategy — maximise the on-device overlap between the polled SPI loop
// (SPI0_pollTransfer runs in the foreground at SFRPAGE=0x20) and the USB ISR:
//
//   * SPI flood        large full-duplex bursts (>57 bytes -> the multi-packet
//                      OUT path) at a deliberately LOW clock, so each transfer
//                      spends a long time in the foreground poll loop. Every
//                      USB SOF (1 ms) and every concurrent packet then lands
//                      mid-SPI. CS is toggled on a real pin so the GPIO path
//                      (which also flips SFRPAGE) runs inside the transaction.
//   * USB noise        a parallel loop of LED toggles + pin reconfig to push
//                      extra OUT packets at the device while SPI is busy.
//   * report drain     continuously consume board.Reports (the pin-status IN
//                      stream the device pushes on its own) — pure RX, always
//                      safe to run concurrently, and more USB traffic.
//   * wedge detector   every SPI transfer is bounded by a timeout. The first
//                      transfer that does not return within the budget means
//                      the board has wedged: we record the stats and stop.
//
// No SPI slave is required — the suspected hang is in the FIFO poll / SFRPAGE
// handling, not in a slave responding. A MOSI->MISO loopback jumper only makes
// the returned bytes meaningful; it is not needed to trip the bug.
//
//   dotnet run --project examples/Periphery.Examples.TreehopperSpiStress -- \
//       [--duration <sec>] [--burst <bytes>] [--clock-mhz <mhz>] \
//       [--cs-pin <n>] [--log-file <path>]
//
// Defaults: run until wedged or Ctrl-C, 200-byte bursts, 0.5 MHz clock, CS on
// pin 0, log mirrored to thopper-spi-stress.log in the working directory.

using System.Diagnostics;
using System.Globalization;

var opts = Options.Parse(args);
using var log = new TeeLog(opts.LogFile);

log.Line($"Treehopper SPI/USB stress harness");
log.Line($"  burst={opts.BurstBytes}B  clock={opts.ClockMhz:0.###}MHz  cs-pin={opts.CsPin}  " +
         $"duration={(opts.Duration is { } d ? $"{d.TotalSeconds:0}s" : "until wedged/Ctrl-C")}");

TreehopperBoard board;
try
{
    board = await TreehopperBoard.OpenFirstAsync();
}
catch (TreehopperException ex)
{
    log.Line($"FATAL: could not open a Treehopper: {ex.Message}");
    return 1;
}

log.Line($"Connected to '{board.DeviceInfo.Name}' (firmware {board.VersionString}).");

// Ctrl-C and the optional --duration both fold into one cancellation source.
using var cts = new CancellationTokenSource();
if (opts.Duration is { } limit) cts.CancelAfter(limit);
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
var ct = cts.Token;

// Shared counters (single-writer per field; long is atomic enough for a report).
long spiCount = 0, noiseCount = 0, reportCount = 0;
var wedged = new TaskCompletionSource<WedgeInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
var sw = Stopwatch.StartNew();

await using (board)
{
    await using var spi = await board.UseSpiAsync(clockMhz: opts.ClockMhz, ct: ct);

    // Configure the CS pin as an output up front so CS toggling is real GPIO work.
    if (opts.CsPin >= 0)
        await board.Pins[opts.CsPin].ConfigureAsync(PinMode.PushPullOutput, ct);

    var tx = new byte[opts.BurstBytes];
    for (int i = 0; i < tx.Length; i++) tx[i] = (byte)(i * 7 + 1);   // non-trivial pattern

    // ── SPI flood: the primary stressor + the wedge detector ────────────────
    var spiTask = Task.Run(async () =>
    {
        while (!ct.IsCancellationRequested)
        {
            using var perXfer = CancellationTokenSource.CreateLinkedTokenSource(ct);
            perXfer.CancelAfter(opts.XferTimeout);
            try
            {
                await spi.TransferAsync(
                    tx,
                    chipSelectPin: opts.CsPin,
                    chipSelectMode: ChipSelectMode.SpiActiveLow,
                    burstMode: SpiBurstMode.NoBurst,
                    ct: perXfer.Token);
                Interlocked.Increment(ref spiCount);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;  // clean shutdown (Ctrl-C / duration), not a wedge
            }
            catch (Exception ex)
            {
                // A per-transfer timeout (or an IO fault) with the outer token
                // still live == the board stopped answering == WEDGED.
                wedged.TrySetResult(new WedgeInfo(
                    Interlocked.Read(ref spiCount),
                    Interlocked.Read(ref noiseCount),
                    Interlocked.Read(ref reportCount),
                    sw.Elapsed,
                    ex.GetType().Name + ": " + ex.Message));
                break;
            }
        }
    }, ct);

    // ── USB noise: extra OUT packets aimed at the device mid-SPI ─────────────
    var noiseTask = Task.Run(async () =>
    {
        bool on = false;
        while (!ct.IsCancellationRequested && !wedged.Task.IsCompleted)
        {
            try
            {
                await board.SetLedAsync(on = !on, ct);
                Interlocked.Increment(ref noiseCount);
            }
            catch (OperationCanceledException) { break; }
            catch { /* board may serialise host calls; ignore and keep pushing */ }
        }
    }, ct);

    // ── Report drain: consume the pin-status IN stream (pure RX traffic) ─────
    var reportTask = Task.Run(async () =>
    {
        try
        {
            await foreach (var _ in board.Reports.WithCancellation(ct))
                Interlocked.Increment(ref reportCount);
        }
        catch (OperationCanceledException) { }
        catch { /* stream ends when the board wedges; the detector owns that */ }
    }, ct);

    // ── Progress ticker ──────────────────────────────────────────────────────
    var tickTask = Task.Run(async () =>
    {
        while (!ct.IsCancellationRequested && !wedged.Task.IsCompleted)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(2), ct); } catch { break; }
            log.Line($"  t+{sw.Elapsed.TotalSeconds,6:0.0}s  spi={Interlocked.Read(ref spiCount),-8} " +
                     $"noise={Interlocked.Read(ref noiseCount),-6} reports={Interlocked.Read(ref reportCount)}");
        }
    }, ct);

    // Wait for either a wedge or clean shutdown.
    var finished = await Task.WhenAny(wedged.Task, spiTask);
    cts.Cancel();
    try { await Task.WhenAll(spiTask, noiseTask, reportTask, tickTask); } catch { /* drained */ }

    if (wedged.Task.IsCompletedSuccessfully)
    {
        var w = wedged.Task.Result;
        log.Line("");
        log.Line("*** BOARD WEDGED ***");
        log.Line($"  after  {w.SpiCount} SPI transfers, {w.NoiseCount} LED toggles, {w.ReportCount} reports");
        log.Line($"  elapsed {w.Elapsed.TotalSeconds:0.0}s");
        log.Line($"  trigger {w.Detail}");
        log.Line("  -> the board is now unresponsive; the MCU watchdog should reset it in ~8s. Re-plug it if it does not.");
        return 2;
    }

    log.Line("");
    log.Line($"Clean shutdown: {Interlocked.Read(ref spiCount)} SPI transfers in {sw.Elapsed.TotalSeconds:0.0}s, " +
             $"no wedge observed.");
}

return 0;

// ─────────────────────────────────────────────────────────────────────────────

readonly record struct WedgeInfo(long SpiCount, long NoiseCount, long ReportCount, TimeSpan Elapsed, string Detail);

sealed record Options(
    int BurstBytes,
    double ClockMhz,
    int CsPin,
    TimeSpan? Duration,
    TimeSpan XferTimeout,
    string LogFile)
{
    public static Options Parse(string[] args)
    {
        int burst = 200;
        double clock = 0.5;
        int csPin = 0;
        TimeSpan? duration = null;
        var xferTimeout = TimeSpan.FromSeconds(2);
        string logFile = "thopper-spi-stress.log";

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => ++i < args.Length ? args[i] : throw new ArgumentException($"missing value for {a}");
            switch (a)
            {
                case "--burst":     burst = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--clock-mhz": clock = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--cs-pin":    csPin = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--duration":  duration = TimeSpan.FromSeconds(double.Parse(Next(), CultureInfo.InvariantCulture)); break;
                case "--exit-after":duration = TimeSpan.FromSeconds(double.Parse(Next(), CultureInfo.InvariantCulture)); break;
                case "--xfer-timeout-ms": xferTimeout = TimeSpan.FromMilliseconds(int.Parse(Next(), CultureInfo.InvariantCulture)); break;
                case "--log-file":  logFile = Next(); break;
                default: throw new ArgumentException($"unknown argument '{a}'");
            }
        }
        return new Options(burst, clock, csPin, duration, xferTimeout, logFile);
    }
}

sealed class TeeLog : IDisposable
{
    private readonly StreamWriter? _file;
    private readonly object _gate = new();

    public TeeLog(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _file = new StreamWriter(path, append: false) { AutoFlush = true };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"(log file unavailable: {ex.Message})");
        }
    }

    public void Line(string s)
    {
        lock (_gate)
        {
            Console.WriteLine(s);
            _file?.WriteLine(s);
        }
    }

    public void Dispose() => _file?.Dispose();
}

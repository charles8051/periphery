// Periphery.Treehopper.Libraries demo — drive an APA102 / SK9822 LED strip off a
// Treehopper's hardware SPI with the pure LedAnimation state machines (ADR-0052 DEC-005).
//
// Wiring: APA102 DI <- Treehopper SPI MOSI (pin 2), CI <- SPI SCK (pin 0), plus 5V + GND.
//
//   dotnet run --project examples/Periphery.Examples.TreehopperLed -- [animation] [options]
//     animation : rainbow (default) | comet | breathe | chase | blink | sequence | solid | off
//     --leds N        number of LEDs in the chain          (default 30)
//     --seconds S     run duration; <= 0 runs until Ctrl+C (default 8)
//     --mhz M         SPI clock in MHz                      (default 6 — see below)
//     --tick-ms N     milliseconds between frames           (default 33 ≈ 30 FPS)
//     --log-file P    mirror the full DEBUG trace to file P (e.g. treehopper-led.log)
//
// Why 6 MHz: the EFM8's SPI FIFO has a silicon bug that can lock the peripheral up
// under heavy USB traffic when it is clocked between 0.8 and 6 MHz. The lock-up freezes
// the firmware's single-threaded main loop, stalls the USB endpoint, and bricks the
// board until it is physically replugged. The original SDK forbids that band
// (HardwareSpi rounds it up to 6 MHz); Periphery's SpiClockByte now does the same, so
// any --mhz in (0.8, 6) is clocked at the safe 6 MHz anyway. Driving the strip at 4 MHz
// without that guard is what was wedging boards — not the frame rate.
//
// No reboot dance: opening the board sends a ConfigureDevice as the reconcile's first
// command, which runs the firmware's full Treehopper_Init() — re-initialising the SPI
// peripheral from scratch (SPI_Disable, then ConfigureSpi brings it back up clean). That
// subsumes the old reboot/disconnect/re-enumerate workaround, so it has been removed.

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Periphery.Diagnostics;

var opts = ParseArgs(args);

using var loggerFactory = BuildLoggerFactory(opts.LogFile);
var log = loggerFactory.CreateLogger("TreehopperLed");

log.LogInformation(
    "Starting · animation={Animation} leds={Leds} spiMhz={Mhz} tickMs={TickMs} duration={Duration} logFile={LogFile}",
    opts.Animation, opts.Leds, opts.Mhz, opts.TickMs,
    opts.Seconds > 0 ? $"{opts.Seconds}s" : "until Ctrl+C", opts.LogFile ?? "(console only)");

// ── Discover ───────────────────────────────────────────────────────────
IReadOnlyList<DeviceInfo> boards;
try
{
    boards = await TreehopperBoard.EnumerateAsync();
}
catch (Exception ex)
{
    log.LogError(ex, "Board enumeration failed.");
    return 1;
}

log.LogInformation("Enumerated {Count} Treehopper board(s): {Boards}",
    boards.Count,
    boards.Count == 0 ? "(none)" : string.Join(", ", boards.Select(b => $"'{b.Name}' [{b.SerialNumber ?? "?"}]")));

if (boards.Count == 0)
{
    log.LogError("No Treehopper board ({Vid}:{Pid}) connected — nothing to drive.",
        TreehopperBoard.Vid, TreehopperBoard.Pid);
    return 1;
}

var info = boards[0];

// ── Open ───────────────────────────────────────────────────────────────
// Bound the open phase: a wedged USB endpoint makes the underlying bulk
// transfer block forever (the WinUSB backend honours cancellation via
// CancelIoEx, but nothing imposes a deadline). Fail fast with an actionable
// message instead of hanging.
using var openCts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
TreehopperBoard board;
log.LogDebug("Opening '{Name}'…", info.Name);
// Pass the app's logger factory into the SDK — the same wiring the kiosk uses. The
// board mints ILogger<TreehopperBoard> + ILogger<UsbDevice> from it, so the SDK's
// open/close, report-producer, and (at Trace) per-transaction diagnostics flow into
// this example's sinks. Metrics flow to the Periphery.Treehopper / Periphery.Usb Meters
// independently (attach a MeterListener / OpenTelemetry to read them).
var openTask = TreehopperBoard.OpenAsync(info, openCts.Token, loggerFactory);
// Wall-clock guard: a deeply wedged endpoint can hang the *device open* itself
// (CreateFile / WinUsb_Initialize) — a synchronous native call the cancellation
// token can't abort. WhenAny gives us a hard deadline regardless of cancellability.
if (await Task.WhenAny(openTask, Task.Delay(TimeSpan.FromSeconds(13))) != openTask)
{
    log.LogError(
        "Board open didn't finish within 13s — the Treehopper's USB endpoint is wedged (the open "
        + "itself is blocking and can't be cancelled). Physically unplug and replug the board, then re-run.");
    return 2;
}
try
{
    board = await openTask;
}
catch (Exception) when (openCts.IsCancellationRequested)
{
    log.LogError(
        "Board open timed out — the Treehopper's USB endpoint is likely wedged. Physically unplug "
        + "and replug the board, then re-run.");
    return 2;
}
catch (Exception ex)
{
    log.LogError(ex, "Failed to open board '{Name}'.", info.Name);
    return 1;
}

await using (board)
{
    log.LogInformation("Connected to '{Name}' (serial {Serial}, firmware {Version}).",
        board.DeviceInfo.Name, board.DeviceInfo.SerialNumber ?? "?", board.VersionString);

    // One SPI lease, opened exclusively for the strip; the strip flushes over it.
    await using var spi = await board.UseSpiAsync(clockMhz: opts.Mhz);
    log.LogInformation("SPI enabled at {Mhz} MHz (mode 0, MSB-first).", opts.Mhz);

    await using var strip = new Apa102Strip(spi, opts.Leds);
    // APA102 wire framing: 4-byte start + 4 bytes/LED + ceil(N/16)-byte end.
    int frameBytes = 4 + (opts.Leds * 4) + ((opts.Leds + 15) / 16);
    log.LogInformation("APA102 strip ready: {Leds} LEDs, {Bytes} bytes/frame.", opts.Leds, frameBytes);

    using var cts = new CancellationTokenSource();
    if (opts.Seconds > 0)
        cts.CancelAfter(TimeSpan.FromSeconds(opts.Seconds));
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; log.LogInformation("Ctrl+C — stopping."); cts.Cancel(); };

    var animation = BuildAnimation(opts.Animation);
    log.LogInformation("Running '{Animation}' at {TickMs} ms/tick (~{Fps:F0} FPS). Ctrl+C to stop.",
        opts.Animation, opts.TickMs, 1000.0 / opts.TickMs);

    await RunInstrumentedAsync(
        strip, animation, frameBytes, TimeSpan.FromMilliseconds(opts.TickMs), log, cts.Token);

    log.LogInformation("Clearing strip.");
    // strip.DisposeAsync() clears the strip; it runs before the SPI lease closes.
}

log.LogInformation("Done.");
return 0;

// ── Instrumented render loop ───────────────────────────────────────────
static async Task RunInstrumentedAsync(
    Apa102Strip strip, LedAnimation animation, int frameBytes, TimeSpan tick, ILogger log, CancellationToken ct)
{
    var current = animation;
    long frames = 0, errors = 0;
    double minMs = double.MaxValue, maxMs = 0, sumMs = 0;
    var runSw = Stopwatch.StartNew();
    var summarySw = Stopwatch.StartNew();
    long framesAtSummary = 0;

    try
    {
        while (!ct.IsCancellationRequested)
        {
            var frame = current.Render(strip.LedCount);

            log.LogDebug("flush #{Frame} start ({Bytes} bytes)…", frames + 1, frameBytes);
            var flushSw = Stopwatch.StartNew();
            try
            {
                await strip.ShowAsync(frame, ct).ConfigureAwait(false);
                flushSw.Stop();
                double ms = flushSw.Elapsed.TotalMilliseconds;
                frames++;
                sumMs += ms;
                minMs = Math.Min(minMs, ms);
                maxMs = Math.Max(maxMs, ms);
                log.LogDebug("flush #{Frame} ok in {Ms:F2} ms.", frames, ms);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                errors++;
                log.LogWarning(ex, "Flush #{Frame} faulted (continuing).", frames + 1);
            }

            current = current.Next();

            if (summarySw.Elapsed >= TimeSpan.FromSeconds(1))
            {
                long delta = frames - framesAtSummary;
                double fps = delta / summarySw.Elapsed.TotalSeconds;
                double avg = frames > 0 ? sumMs / frames : 0;
                log.LogInformation(
                    "metrics: {Fps:F1} FPS · {Frames} frames · flush avg/min/max {Avg:F2}/{Min:F2}/{Max:F2} ms · {Errors} error(s)",
                    fps, frames, avg, minMs == double.MaxValue ? 0 : minMs, maxMs, errors);
                framesAtSummary = frames;
                summarySw.Restart();
            }

            try
            {
                await Task.Delay(tick, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
    catch (OperationCanceledException)
    {
        // expected on timeout / Ctrl+C
    }

    runSw.Stop();
    double avgAll = frames > 0 ? sumMs / frames : 0;
    double fpsAll = runSw.Elapsed.TotalSeconds > 0 ? frames / runSw.Elapsed.TotalSeconds : 0;
    var level = errors > 0 ? LogLevel.Warning : LogLevel.Information;
    log.Log(level,
        "Run complete: {Frames} frames in {Sec:F1}s ({Fps:F1} FPS) · flush avg/min/max {Avg:F2}/{Min:F2}/{Max:F2} ms · {Errors} error(s).",
        frames, runSw.Elapsed.TotalSeconds, fpsAll, avgAll, minMs == double.MaxValue ? 0 : minMs, maxMs, errors);

    if (frames > 0 && errors == 0)
        log.LogInformation(
            "All flushes succeeded. If the strip stayed dark, suspect wiring " +
            "(DI<-MOSI pin 2, CI<-SCK pin 0), strip power, or SPI mode.");
}

// ── Animation factory ──────────────────────────────────────────────────
static LedAnimation BuildAnimation(string name) => name switch
{
    "comet"    => new LedAnimation.Comet(Rgb.Cyan),
    "breathe"  => new LedAnimation.Breathe(Rgb.Purple),
    "chase"    => new LedAnimation.Chase(Rgb.Orange),
    "blink"    => new LedAnimation.Blink(Rgb.Green),
    "solid"    => new LedAnimation.Solid(Rgb.White),
    "off"      => new LedAnimation.Off(),
    "sequence" => LedAnimation.Sequence.Create(
                      (new LedAnimation.Blink(Rgb.Green), 24),
                      (new LedAnimation.Comet(Rgb.Cyan),  90),
                      (new LedAnimation.Solid(Rgb.Green),  1)),
    _          => new LedAnimation.Rainbow(),
};

// ── Logging setup ──────────────────────────────────────────────────────
static ILoggerFactory BuildLoggerFactory(string? logFile) =>
    LoggerFactory.Create(b =>
    {
        b.SetMinimumLevel(LogLevel.Trace);
        b.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss.fff "; });
        // Console stays at Information (clean headline + 1 Hz metrics); the file sink,
        // when enabled, captures the full DEBUG trace including per-flush timings.
        b.AddFilter<ConsoleLoggerProvider>(null, LogLevel.Information);
        if (!string.IsNullOrEmpty(logFile))
            b.AddProvider(new SinkLoggerProvider(
                new FileLogSink(logFile, "Periphery.Examples.TreehopperLed"), LogLevel.Debug));
    });

// ── Args ───────────────────────────────────────────────────────────────
static Options ParseArgs(string[] args)
{
    string animation = "rainbow";
    int leds = 30, seconds = 8;
    double mhz = 6;
    string? logFile = null;
    int tickMs = 33;

    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--leds" when i + 1 < args.Length:     leds = int.Parse(args[++i]); break;
            case "--seconds" when i + 1 < args.Length:  seconds = int.Parse(args[++i]); break;
            case "--mhz" when i + 1 < args.Length:      mhz = double.Parse(args[++i]); break;
            case "--tick-ms" when i + 1 < args.Length:  tickMs = int.Parse(args[++i]); break;
            case "--log-file" when i + 1 < args.Length: logFile = args[++i]; break;
            default:
                if (!args[i].StartsWith("--", StringComparison.Ordinal))
                    animation = args[i].ToLowerInvariant();
                break;
        }
    }

    return new Options(animation, Math.Max(1, leds), seconds, mhz, logFile, Math.Max(1, tickMs));
}

internal readonly record struct Options(
    string Animation, int Leds, int Seconds, double Mhz, string? LogFile, int TickMs);

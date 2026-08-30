// Treehopper UART transmit exerciser / lock-up stressor.
// ======================================================
// Sends UART messages (the firmware's UART_CMD_TX path: a foreground
// `while(!SCON0_TI)` busy-wait per byte, with UART interrupts disabled). At low
// baud a 63-byte message is a long busy-wait, so a USB packet / SOF landing
// mid-transmit has a wide window to provoke the same wedge we suspect on SPI.
// UART TX is on P0.4 (= Treehopper pin 5); nothing needs to be connected to it.
//
//   dotnet run --project scratch/UartEmit -- \
//       [--baud 9600] [--bytes 63] [--count N | --duration <sec>] \
//       [--gap-ms 0] [--noise] [--send-timeout-ms 2000] [--log-file <path>]
//
// Defaults: 9600 baud (long busy-wait), 63-byte messages, run until wedged or
// Ctrl-C, LED/pin USB noise on, 2 s per-send timeout = wedge detector.

using System.Diagnostics;
using System.Globalization;
using System.Text;

int baud = 9600;
int bytes = 63;
int? count = null;
double? durationSec = null;
int gapMs = 0;
bool noise = true;
bool readBack = false;
int sendTimeoutMs = 2000;
string logFile = "thopper-uart-stress.log";

for (int i = 0; i < args.Length; i++)
{
    string Next() => ++i < args.Length ? args[i] : throw new ArgumentException($"missing value for {args[i - 1]}");
    switch (args[i])
    {
        case "--baud": baud = int.Parse(Next(), CultureInfo.InvariantCulture); break;
        case "--bytes": bytes = int.Parse(Next(), CultureInfo.InvariantCulture); break;
        case "--count": count = int.Parse(Next(), CultureInfo.InvariantCulture); break;
        case "--duration": durationSec = double.Parse(Next(), CultureInfo.InvariantCulture); break;
        case "--gap-ms": gapMs = int.Parse(Next(), CultureInfo.InvariantCulture); break;
        case "--noise": noise = true; break;
        case "--no-noise": noise = false; break;
        case "--read": readBack = true; break;
        case "--send-timeout-ms": sendTimeoutMs = int.Parse(Next(), CultureInfo.InvariantCulture); break;
        case "--log-file": logFile = Next(); break;
        default: throw new ArgumentException($"unknown argument '{args[i]}'");
    }
}

if (bytes > 63) bytes = 63;  // firmware cap

using var log = new TeeLog(logFile);
log.Line($"Treehopper UART stress: {baud} baud, {bytes}-byte messages, noise={noise}, " +
         $"send-timeout={sendTimeoutMs}ms, {(count is int c ? $"{c} messages" : durationSec is double d ? $"{d:0}s" : "until wedged/Ctrl-C")}");

TreehopperBoard board;
try { board = await TreehopperBoard.OpenFirstAsync(); }
catch (TreehopperException ex) { log.Line($"FATAL: {ex.Message}"); return 1; }

log.Line($"Connected to '{board.DeviceInfo.Name}' (fw {board.VersionString}).");

using var cts = new CancellationTokenSource();
if (durationSec is double lim) cts.CancelAfter(TimeSpan.FromSeconds(lim));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
var ct = cts.Token;

long sent = 0, noiseCount = 0, reads = 0;
var wedged = new TaskCompletionSource<WedgeInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
var sw = Stopwatch.StartNew();

await using (board)
{
    await using var uart = await board.UseUartAsync(baud, ct);

    // Build a recognizable message body padded to `bytes`.
    byte[] Message(long n)
    {
        string s = $"THOPPER-UART {n} ";
        var buf = new byte[bytes];
        var ascii = Encoding.ASCII.GetBytes(s);
        for (int i = 0; i < bytes; i++) buf[i] = ascii.Length > 0 ? ascii[i % ascii.Length] : (byte)'.';
        return buf;
    }

    var sendTask = Task.Run(async () =>
    {
        while (!ct.IsCancellationRequested)
        {
            if (count is int max && Interlocked.Read(ref sent) >= max) break;
            using var perSend = CancellationTokenSource.CreateLinkedTokenSource(ct);
            perSend.CancelAfter(sendTimeoutMs);
            try
            {
                await uart.SendAsync(Message(sent), perSend.Token);
                Interlocked.Increment(ref sent);
                if (readBack)
                {
                    // Loopback: drain what came back, exercising the firmware
                    // UART_CMD_RX path. A hang here also trips the wedge detector.
                    await uart.ReceiveAsync(perSend.Token);
                    Interlocked.Increment(ref reads);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                wedged.TrySetResult(new WedgeInfo(
                    Interlocked.Read(ref sent), Interlocked.Read(ref noiseCount), sw.Elapsed,
                    ex.GetType().Name + ": " + ex.Message));
                break;
            }
            if (gapMs > 0) { try { await Task.Delay(gapMs, ct); } catch { break; } }
        }
    }, ct);

    var noiseTask = Task.Run(async () =>
    {
        bool on = false;
        while (noise && !ct.IsCancellationRequested && !wedged.Task.IsCompleted)
        {
            try { await board.SetLedAsync(on = !on, ct); Interlocked.Increment(ref noiseCount); }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }, ct);

    var tickTask = Task.Run(async () =>
    {
        while (!ct.IsCancellationRequested && !wedged.Task.IsCompleted)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(2), ct); } catch { break; }
            log.Line($"  t+{sw.Elapsed.TotalSeconds,6:0.0}s  sent={Interlocked.Read(ref sent),-8} reads={Interlocked.Read(ref reads),-8} noise={Interlocked.Read(ref noiseCount)}");
        }
    }, ct);

    await Task.WhenAny(wedged.Task, sendTask);
    cts.Cancel();
    try { await Task.WhenAll(sendTask, noiseTask, tickTask); } catch { }

    if (wedged.Task.IsCompletedSuccessfully)
    {
        var w = wedged.Task.Result;
        log.Line("");
        log.Line("*** BOARD WEDGED ***");
        log.Line($"  after {w.Sent} UART messages, {w.NoiseCount} LED toggles, {w.Elapsed.TotalSeconds:0.0}s");
        log.Line($"  trigger {w.Detail}");
        log.Line($"  -> board frozen; dump it over C2 now (python scratch/jlink/jdump.py).");
        return 2;
    }

    log.Line("");
    log.Line($"Clean: {Interlocked.Read(ref sent)} UART messages in {sw.Elapsed.TotalSeconds:0.0}s, no wedge.");
}
return 0;

readonly record struct WedgeInfo(long Sent, long NoiseCount, TimeSpan Elapsed, string Detail);

sealed class TeeLog : IDisposable
{
    private readonly StreamWriter? _file;
    private readonly object _gate = new();
    public TeeLog(string path)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(path)!); _file = new StreamWriter(path, false) { AutoFlush = true }; }
        catch (Exception ex) { Console.Error.WriteLine($"(log file unavailable: {ex.Message})"); }
    }
    public void Line(string s) { lock (_gate) { Console.WriteLine(s); _file?.WriteLine(s); } }
    public void Dispose() => _file?.Dispose();
}

#:property TargetFramework=net10.0
#:package Periphery@1.0.0-alpha.*
#:package System.IO.Ports@9.0.0

// Demonstrates a service layer built on DeviceSessionHost<TSession>.
//
// DeviceSessionHost manages session creation and withdrawal automatically.
// Callers use TryGetCurrentSession / GetRequiredSession for fail-fast access
// without depending on reconnect-loop internals.
//
// This pattern is well-suited for injected service objects, view-models, or any
// layer that should not depend on reconnect-loop internals.
//
// Run: dotnet run serial-facade.cs

using System.IO.Ports;
using Periphery;

// ── Session type ───────────────────────────────────────────────────────────────

class SerialSession(SerialPort port, string portName) : IAsyncDisposable
{
    public string PortName { get; } = portName;
    public void WriteLine(string line) => port.WriteLine(line);
    public string ReadLine() => port.ReadLine();

    public ValueTask DisposeAsync()
    {
        port.Close();
        port.Dispose();
        return ValueTask.CompletedTask;
    }
}

// ── Service layer ──────────────────────────────────────────────────────────────
// In a real application this would be a DI-registered singleton that receives
// the session host via constructor injection.

class ScannerService(DeviceSessionHost<SerialSession> host)
{
    // Sends a command and returns the response line.
    // Throws InvalidOperationException when the scanner is not connected.
    public Task<string> QueryAsync(string command, CancellationToken ct = default)
    {
        var session = host.GetRequiredSession();
        session.WriteLine(command);
        return Task.Run(session.ReadLine, ct);
    }

    // Best-effort ping — returns false when the device is absent, never throws.
    public bool TryPing()
    {
        if (!host.TryGetCurrentSession(out var session))
            return false;

        session.WriteLine("PING");
        return true;
    }
}

// ── Main ───────────────────────────────────────────────────────────────────────

Console.WriteLine("╔══════════════════════════════════════════╗");
Console.WriteLine("║  DeviceSessionHost — Serial Port Example ║");
Console.WriteLine("╚══════════════════════════════════════════╝");
Console.WriteLine();
Console.WriteLine("Plug in a serial device. Press any key to stop.");
Console.WriteLine();

var profile = new DeviceProfile(f => f.OfCategory(DeviceCategory.Ports));

await using var host = await DeviceSessionHost<SerialSession>.StartAsync(
    profile,

    createSession: (info, ct) =>
    {
        var portName = info.PortName!.Value.Value;
        Console.WriteLine($"  [+] Opening {portName}");

        var port = new SerialPort(portName, baudRate: 115_200)
        {
            ReadTimeout  = 500,
            WriteTimeout = 500,
        };
        port.Open();
        return Task.FromResult(new SerialSession(port, portName));
    },

    onSessionEnded: async session =>
    {
        Console.WriteLine($"  [-] Session ended for {session.PortName}.");
        await session.DisposeAsync().ConfigureAwait(false);
    });

var scanner = new ScannerService(host);

Console.WriteLine($"  Initial status: {host.Status.GetType().Name}");

// ── Periodic poll loop ─────────────────────────────────────────────────────────

using var cts = new CancellationTokenSource();

var pollTask = Task.Run(async () =>
{
    while (!cts.Token.IsCancellationRequested)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            break;
        }

        // GetRequiredSession — throws InvalidOperationException if device not connected.
        try
        {
            var response = await scanner.QueryAsync("STATUS?", cts.Token).ConfigureAwait(false);
            Console.WriteLine($"  [<] STATUS response: {response}");
        }
        catch (InvalidOperationException ex)
        {
            // Device absent or session starting — caller decides how to handle.
            Console.WriteLine($"  [!] Device unavailable: {ex.Message}");
        }

        // TryGetCurrentSession — returns false when unavailable, never throws.
        var pinged = scanner.TryPing();
        Console.WriteLine($"  [PING] succeeded={pinged}");

        Console.WriteLine($"  [STATUS] HasSession={host.HasSession}  " +
                          $"device={host.DeviceInfo?.Name ?? "(none)"}");
    }
});

await Task.Run(() => Console.ReadKey(intercept: true));

cts.Cancel();
try { await pollTask.ConfigureAwait(false); } catch { }

Console.WriteLine();
Console.WriteLine("Stopping…");

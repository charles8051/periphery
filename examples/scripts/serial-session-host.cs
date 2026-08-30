#:property TargetFramework=net10.0
#:package Periphery@1.0.0-alpha.*
#:package System.IO.Ports@9.0.0

// Demonstrates wrapping a SerialPort with DeviceSessionHost<TSession>.
//
// The host creates a SerialSession object each time the device connects and
// publishes it through a typed status observable. Consumers anywhere in the
// application can call WaitForSessionAsync to obtain a reference to the session
// without coupling to the connection lifecycle directly.
//
// Lifecycle:
//   createSession      — open the port, build the session object
//   whileSessionActive — read loop for the duration of the session
//   onSessionEnded     — close / clean up the port
//
// Run: dotnet run serial-session-host.cs

using System.IO.Ports;
using Periphery;

// ── Session type ───────────────────────────────────────────────────────────────
// Encapsulates the open SerialPort so callers never touch the raw device.
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
        // Opens the port and returns the session. Throw here to abort
        // the connection (host backs off and retries).
        var portName = info.PortName!.Value.Value;
        Console.WriteLine($"  [+] Opening {portName} ({info.Name ?? "unnamed"})");

        var port = new SerialPort(portName, baudRate: 115_200)
        {
            ReadTimeout  = 500,
            WriteTimeout = 500,
        };
        port.Open();
        port.WriteLine("HELLO");
        Console.WriteLine("  [+] Session started.");

        return Task.FromResult(new SerialSession(port, portName));
    },

    whileSessionActive: async (session, ct) =>
    {
        // Background read loop — runs for the lifetime of the session.
        // Non-CT exception → session ends + reconnect.
        // Clean return or OperationCanceledException → no reconnect.
        Console.WriteLine("  [~] Entering read loop…");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var line = await Task.Run(() => session.ReadLine(), ct)
                    .ConfigureAwait(false);

                Console.WriteLine($"  [<] {line}");
            }
            catch (TimeoutException)
            {
                // No data within ReadTimeout — loop again.
            }
        }
    },

    onSessionEnded: async session =>
    {
        Console.WriteLine($"  [-] Session ended for {session.PortName}.");
        await session.DisposeAsync().ConfigureAwait(false);
    });

host.StatusChanged += (_, status) =>
{
    var label = status switch
    {
        SessionActive<SerialSession> s
            => $"SessionActive  port={s.Session.PortName}",
        SessionStarting<SerialSession>
            => "SessionStarting…",
        SessionUnavailable<SerialSession> u
            => $"SessionUnavailable  attempt={u.Attempt}  error={u.LastError?.Message}",
        DeviceAbsent<SerialSession>
            => "DeviceAbsent — waiting for device",
        _ => status.ToString()!,
    };
    Console.WriteLine($"  [STATUS] {label}");
};

// Spawn a task that waits for the first session then sends a command.
_ = Task.Run(async () =>
{
    using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var session = await host.WaitForSessionAsync(timeoutCts.Token).ConfigureAwait(false);

    Console.WriteLine($"  [CONSUMER] Got session for {session.PortName}.");
    session.WriteLine("STATUS?");
});

await Task.Run(() => Console.ReadKey(intercept: true));

Console.WriteLine();
Console.WriteLine("Stopping…");

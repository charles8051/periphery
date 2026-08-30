#:property TargetFramework=net10.0
#:package Periphery@1.0.0-alpha.*
#:package System.IO.Ports@9.0.0

// Demonstrates wrapping a SerialPort with the non-generic DeviceProxy.
// Resources are held in closure-captured state — no IAsyncDisposable wrapper needed.
//
// Lifecycle:
//   onActivated   — open the port, send an initialisation command
//   whileOpen     — read lines from the port until disconnected
//   onDeactivated — close and free the port
//
// Run: dotnet run serial-device-handle.cs

using System.IO.Ports;
using Periphery;

Console.WriteLine("╔══════════════════════════════════════════╗");
Console.WriteLine("║  DeviceProxy — Serial Port Example      ║");
Console.WriteLine("╚══════════════════════════════════════════╝");
Console.WriteLine();
Console.WriteLine("Plug in a serial device. Press any key to stop.");
Console.WriteLine();

// Match any serial (COM) port device. Narrow this with
// .WithUsbId("VID", "PID") or .WithName("My Device") in production.
var profile = new DeviceProfile(f => f.OfCategory(DeviceCategory.Ports));

// The non-generic handle manages no TDevice — you own the resource.
SerialPort? port = null;

await using var handle = await DeviceProxy.OpenAsync(
    profile,

    onActivated: (info, ct) =>
    {
        // Called when the device becomes active. Throw here to abort
        // the connection (the handle will back off and retry).
        var portName = info.PortName!.Value.Value;
        Console.WriteLine($"  [+] Opening {portName} ({info.Name ?? "unnamed"})");

        port = new SerialPort(portName, baudRate: 115_200)
        {
            ReadTimeout  = 500,
            WriteTimeout = 500,
        };
        port.Open();

        // Send a wake / identify command to the device.
        port.WriteLine("HELLO");
        Console.WriteLine("  [+] Port open and initialised.");
        return Task.CompletedTask;
    },

    whileOpen: async (info, ct) =>
    {
        // Runs for the duration of the connection on a background thread.
        // A non-OperationCanceledException here triggers close + reconnect.
        // A clean return (or OperationCanceledException) leaves state as-is.
        Console.WriteLine("  [~] Entering read loop…");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // ReadLine blocks for up to ReadTimeout ms; wrap in a
                // task so the cancellation token can interrupt it.
                var line = await Task.Run(() => port!.ReadLine(), ct)
                    .ConfigureAwait(false);

                Console.WriteLine($"  [<] {line}");
            }
            catch (TimeoutException)
            {
                // No data within ReadTimeout — loop again.
            }
            // OperationCanceledException propagates; the base class treats
            // it as a clean exit (no reconnect triggered).
        }
    },

    onDeactivated: _ =>
    {
        // Called when the device disconnects or the handle is disposed.
        // Runs BEFORE the next reconnect attempt.
        Console.WriteLine("  [-] Closing port.");
        port?.Close();
        port?.Dispose();
        port = null;
        return Task.CompletedTask;
    });

handle.PropertyChanged += (_, e) =>
{
    if (e.PropertyName == nameof(handle.IsOpen))
        Console.WriteLine(handle.IsOpen
            ? "  [STATUS] Handle is open."
            : "  [STATUS] Handle closed — waiting for device…");
};

handle.OpenFailed += (_, ex) =>
    Console.WriteLine($"  [!] Open failed: {ex.Message} — will retry.");

await Task.Run(() => Console.ReadKey(intercept: true));

Console.WriteLine();
Console.WriteLine("Stopping…");
// await using disposes the handle, which cancels the worker and
// calls onDeactivated if the port is currently open.

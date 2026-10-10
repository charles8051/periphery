// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.ComponentModel;
using System.IO.Pipelines;
using Periphery.Cli.Serial;
using Periphery.Cli.Serial.Core;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Periphery.Cli.Commands;

/// <summary>
/// <c>periphery serial</c>: an interactive terminal on one serial port (ADR-0043 Phase 3).
/// An enumerated port runs under <see cref="DeviceSessionHost{TSession}"/>, so unplugging
/// the adapter reports a status line and replugging resumes on the same device.
/// </summary>
/// <remarks>
/// Port data is written to stdout and status lines to stderr. Keystrokes are sent as they
/// are typed, Ctrl+C included; Ctrl+] quits. With stdin redirected, its bytes are sent
/// unchanged once a session is up, and the command exits <c>--linger</c> after end of input.
/// </remarks>
internal sealed class SerialCommand : AsyncCommand<SerialCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("OS port name: COM4, /dev/ttyUSB0. Default: the only connected serial port.")]
        [CommandArgument(0, "[port]")]
        public string? Port { get; init; }

        [Description("Select by exact device Id.")]
        [CommandOption("--id")]
        public string? Id { get; init; }

        [Description("Select by name substring.")]
        [CommandOption("--name")]
        public string? Name { get; init; }

        [Description("Baud rate. Default: 115200.")]
        [CommandOption("-b|--baud <RATE>")]
        public int Baud { get; init; } = SerialLineSettings.Default.BaudRate;

        [Description("Data bits, 5 to 8. Default: 8.")]
        [CommandOption("--data-bits <N>")]
        public int DataBits { get; init; } = SerialLineSettings.Default.DataBits;

        [Description("none, even, odd, mark or space. Default: none.")]
        [CommandOption("--parity <PARITY>")]
        public string Parity { get; init; } = "none";

        [Description("1, 1.5 or 2. Default: 1.")]
        [CommandOption("--stop-bits <N>")]
        public string StopBits { get; init; } = "1";

        [Description("none, rtscts, xonxoff or both. Default: none.")]
        [CommandOption("--handshake <MODE>")]
        public string Handshake { get; init; } = "none";

        [Description("What Enter sends: cr, lf or crlf. Default: cr.")]
        [CommandOption("--eol <EOL>")]
        public string Eol { get; init; } = "cr";

        [Description("Assert DTR while the port is open.")]
        [CommandOption("--dtr")]
        public bool Dtr { get; init; }

        [Description("Assert RTS while the port is open. Ignored under hardware flow control.")]
        [CommandOption("--rts")]
        public bool Rts { get; init; }

        [Description("Print typed characters locally.")]
        [CommandOption("--echo")]
        public bool Echo { get; init; }

        [Description("Show received bytes as hex.")]
        [CommandOption("--hex")]
        public bool Hex { get; init; }

        [Description("With redirected stdin, keep printing for this many milliseconds after end of input. Default: 1000.")]
        [CommandOption("--linger <MS>")]
        public int LingerMs { get; init; } = 1000;

        public override ValidationResult Validate()
        {
            if (Baud <= 0)
                return ValidationResult.Error("--baud must be positive.");
            if (DataBits is < 5 or > 8)
                return ValidationResult.Error("--data-bits must be 5 to 8.");
            if (!SerialLineSettings.TryParseParity(Parity, out _))
                return ValidationResult.Error("--parity must be none, even, odd, mark or space.");
            if (!SerialLineSettings.TryParseStopBits(StopBits, out _))
                return ValidationResult.Error("--stop-bits must be 1, 1.5 or 2.");
            if (!SerialLineSettings.TryParseHandshake(Handshake, out _))
                return ValidationResult.Error("--handshake must be none, rtscts, xonxoff or both.");
            if (!TryParseEol(Eol, out _))
                return ValidationResult.Error("--eol must be cr, lf or crlf.");
            if (LingerMs < 0)
                return ValidationResult.Error("--linger cannot be negative.");
            return ValidationResult.Success();
        }

        internal SerialLineSettings Line()
        {
            SerialLineSettings.TryParseParity(Parity, out var parity);
            SerialLineSettings.TryParseStopBits(StopBits, out var stopBits);
            SerialLineSettings.TryParseHandshake(Handshake, out var handshake);
            return new SerialLineSettings(Baud, DataBits, parity, stopBits, handshake);
        }
    }

    internal static bool TryParseEol(string text, out LineEnding ending)
    {
        ending = text.ToLowerInvariant() switch
        {
            "cr" => LineEnding.Cr,
            "lf" => LineEnding.Lf,
            "crlf" => LineEnding.CrLf,
            _ => (LineEnding)(-1),
        };
        return ending != (LineEnding)(-1);
    }

    protected override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var err = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(Console.Error) });

        var candidates = await Devices.Enumerate()
            .OfCategory(DeviceCategory.Ports)
            .ToListAsync(cancellationToken);
        var selection = SerialPortSelection.Select(candidates, settings.Port, settings.Id, settings.Name);
        if (selection.Error is not null)
        {
            err.MarkupLine($"[red]{Markup.Escape(selection.Error)}[/]");
            return 1;
        }

        var line = settings.Line();
        TryParseEol(settings.Eol, out var eol);
        string portName = selection.Device?.PortName?.Value ?? selection.Unsupervised!.Value.Value;

        await using var rawOut = Console.IsOutputRedirected ? Console.OpenStandardOutput() : null;
        var output = new TerminalOutput(
            err,
            Console.Out,
            rawOut,
            settings.Hex,
            sharedScreen: !Console.IsOutputRedirected && !Console.IsErrorRedirected,
            new StatusNarrator(portName, line));

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await using var connection = selection.Device is { } device
            ? await SupervisedConnection.StartAsync(device, portName, line, settings, output, stop.Token)
            : UnsupervisedConnection.Open(portName, line, settings, output, stop);
        if (connection is null) return 1;

        var input = Console.IsInputRedirected
            ? ForwardRedirectedInputAsync(connection, output, TimeSpan.FromMilliseconds(settings.LingerMs), stop.Token)
            : ForwardKeystrokesAsync(connection, output, new KeyEncoder(eol), settings.Echo, stop.Token);

        try
        {
            await input.WaitAsync(stop.Token);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // Ctrl+C with redirected input, or an unsupervised port that died.
        }

        output.Finish();
        return connection.Failed ? 1 : 0;
    }

    /// <summary>
    /// Reads keys on a dedicated thread. <see cref="Console.ReadKey(bool)"/> cannot be
    /// cancelled, so the thread is a background thread and is abandoned on exit.
    /// </summary>
    private static Task ForwardKeystrokesAsync(
        IConnection connection, TerminalOutput output, KeyEncoder encoder, bool echo, CancellationToken ct)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            // Ctrl+C is a byte for the device, not a signal for this process.
            bool treatControlC = Console.TreatControlCAsInput;
            Console.TreatControlCAsInput = true;
            try
            {
                bool notified = false;
                while (!ct.IsCancellationRequested)
                {
                    (encoder, var action) = encoder.Encode(Console.ReadKey(intercept: true));
                    if (action is KeyAction.Quit) break;
                    if (action is not KeyAction.Send send) continue;

                    if (echo)
                        output.WriteData(send.Bytes.SequenceEqual(KeyEncoder.LineEndingBytes(encoder.LineEnding))
                            ? "\r\n"u8
                            : send.Bytes);

                    if (connection.TryWrite(send.Bytes, out string? problem))
                    {
                        notified = false;
                    }
                    else if (!notified)
                    {
                        output.Status(problem);
                        notified = true;
                    }
                }
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
                return;
            }
            finally
            {
                Console.TreatControlCAsInput = treatControlC;
            }

            done.TrySetResult();
        })
        {
            IsBackground = true,
            Name = "periphery serial keyboard",
        };
        thread.Start();
        return done.Task;
    }

    private static async Task ForwardRedirectedInputAsync(
        IConnection connection, TerminalOutput output, TimeSpan linger, CancellationToken ct)
    {
        await using var stdin = Console.OpenStandardInput();
        var buffer = new byte[4096];
        int read;
        while ((read = await stdin.ReadAsync(buffer, ct)) > 0)
        {
            byte[] chunk = buffer[..read];
            while (true)
            {
                await connection.WaitForSessionAsync(ct);
                if (connection.TryWrite(chunk, out string? problem)) break;
                output.Status(problem);
            }
        }

        await Task.Delay(linger, ct);
    }

    /// <summary>The port the keyboard writes to, however it is kept open.</summary>
    private interface IConnection : IAsyncDisposable
    {
        /// <summary>The port died and could not be followed.</summary>
        bool Failed { get; }

        Task WaitForSessionAsync(CancellationToken ct);

        bool TryWrite(byte[] bytes, [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? problem);
    }

    private static bool TryWrite(SerialSession? session, byte[] bytes, out string? problem)
    {
        if (session is null)
        {
            problem = "Not connected. Input dropped.";
            return false;
        }

        try
        {
            session.Write(bytes);
            problem = null;
            return true;
        }
        catch (TimeoutException)
        {
            problem = $"Write to {session.PortName} timed out. Is flow control holding it back?";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException
                                       or UnauthorizedAccessException)
        {
            problem = $"Write to {session.PortName} failed: {ex.Message}";
        }

        return false;
    }

    /// <summary>
    /// Copies received bytes to the terminal until the pipe ends. A clean end still
    /// throws, so the session host treats it as a fault and reopens the port.
    /// </summary>
    private static async Task PumpAsync(SerialSession session, TerminalOutput output, CancellationToken ct)
    {
        PipeReader reader = session.Pipe.Input;
        while (true)
        {
            var result = await reader.ReadAsync(ct);
            foreach (var segment in result.Buffer)
                output.WriteData(segment.Span);
            reader.AdvanceTo(result.Buffer.End);

            if (result.IsCompleted)
                throw new IOException($"{session.PortName} closed.");
        }
    }

    private sealed class SupervisedConnection : IConnection
    {
        private readonly DeviceSessionHost<SerialSession> _host;
        private readonly TerminalOutput _output;

        private SupervisedConnection(DeviceSessionHost<SerialSession> host, TerminalOutput output)
        {
            _host = host;
            _output = output;
        }

        public bool Failed => false;

        internal static async Task<IConnection?> StartAsync(
            DeviceInfo device, string portName, SerialLineSettings line, Settings settings,
            TerminalOutput output, CancellationToken ct)
        {
            var host = await DeviceSessionHost<SerialSession>.ForDeviceAsync(
                device,
                // The port name is read from each arrival, not fixed at start: a driver
                // can hand a replugged adapter a different one.
                createSession: (arrived, _) => Task.FromResult(SerialSession.Open(
                    arrived.PortName?.Value ?? portName, line, settings.Dtr, settings.Rts)),
                onSessionEnded: session => session.DisposeAsync().AsTask(),
                whileSessionActive: (session, token) => PumpAsync(session, output, token),
                ct: ct);

            host.StatusChanged += (_, status) => output.Narrate(status);
            // A status raised before the subscription is reported here; the narrator
            // suppresses the repeat if it was not.
            output.Narrate(host.Status);
            return new SupervisedConnection(host, output);
        }

        public Task WaitForSessionAsync(CancellationToken ct) => _host.WaitForSessionAsync(ct);

        public bool TryWrite(byte[] bytes, out string? problem) =>
            SerialCommand.TryWrite(_host.CurrentSession, bytes, out problem);

        public ValueTask DisposeAsync() => _host.DisposeAsync();
    }

    /// <summary>
    /// A port that matched no enumerated device. It is opened once, and the command
    /// ends when it fails.
    /// </summary>
    private sealed class UnsupervisedConnection : IConnection
    {
        private readonly SerialSession _session;
        private readonly Task _pump;
        private readonly CancellationTokenSource _stop;

        private UnsupervisedConnection(SerialSession session, Task pump, CancellationTokenSource stop)
        {
            _session = session;
            _pump = pump;
            _stop = stop;
        }

        public bool Failed => _pump.IsFaulted;

        internal static IConnection? Open(
            string portName, SerialLineSettings line, Settings settings, TerminalOutput output,
            CancellationTokenSource stop)
        {
            SerialSession session;
            try
            {
                session = SerialSession.Open(portName, line, settings.Dtr, settings.Rts);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                           or InvalidOperationException)
            {
                output.Status($"Cannot open {portName}: {ex.Message}");
                return null;
            }

            output.Status($"Connected to {portName} at {line.Describe()}. It is not an enumerated device, "
                          + "so a replug ends the session. Ctrl+] to quit.");

            var pump = PumpAsync(session, output, stop.Token).ContinueWith(
                task =>
                {
                    if (task.IsFaulted)
                        output.Status($"{portName} failed: {task.Exception!.GetBaseException().Message}");
                    stop.Cancel();
                    if (task.IsFaulted) throw task.Exception!.GetBaseException();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return new UnsupervisedConnection(session, pump, stop);
        }

        public Task WaitForSessionAsync(CancellationToken ct) => Task.CompletedTask;

        public bool TryWrite(byte[] bytes, out string? problem) =>
            SerialCommand.TryWrite(_session, bytes, out problem);

        public async ValueTask DisposeAsync()
        {
            // Stop the pump before closing the port, or it would report the close as a failure.
            _stop.Cancel();
            try { await _pump; }
            catch { /* Reported when it happened. */ }
            await _session.DisposeAsync();
        }
    }
}

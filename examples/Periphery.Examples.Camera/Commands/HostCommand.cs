using Periphery.Examples.Camera.Common;

namespace Periphery.Examples.Camera.Commands;

/// <summary>
/// Demonstrates reconnect-resilient lifecycle via
/// <see cref="DeviceSessionHost{T}"/>. The host owns a profile that
/// matches a camera and a session factory that opens a CameraSession
/// against whichever device is currently active.
///
/// While running, unplug and replug the camera — you should see status
/// transitions: <c>SessionActive</c> → <c>DeviceAbsent</c> →
/// <c>SessionStarting</c> → <c>SessionActive</c>.
/// </summary>
internal static class HostCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        int seconds = Args.GetIntOption(args, defaultValue: 30, "--seconds", "-s");
        string? name = Args.GetOption(args, "--device", "-d");

        // Build a profile. If --device NAME is supplied, narrow to cameras
        // whose Name contains that substring; otherwise match any camera.
        var profile = name is null
            ? new DeviceProfile(f => f.OfCategory(DeviceCategory.Camera), "Any camera")
            : new DeviceProfile(f => f
                .OfCategory(DeviceCategory.Camera)
                .WithName(name), $"Camera matching '{name}'");

        Console.WriteLine($"Profile: {profile.Name}");
        Console.WriteLine($"Running for {seconds}s. Unplug/replug the camera to observe reconnect.");
        Console.WriteLine($"Press Ctrl-C to exit early.");
        Console.WriteLine();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        // The session factory: receives the live DeviceInfo each time the
        // host needs to bring a session up, returns a configured CameraSession.
        Func<DeviceInfo, CancellationToken, Task<CameraSession>> createSession =
            async (deviceInfo, ct) =>
            {
                var snap = await CameraDevice.ReadSnapshotAsync(deviceInfo, ct).ConfigureAwait(false);
                var format = snap.Formats
                    .OrderByDescending(f => f.Width * f.Height)
                    .ThenByDescending(f => f.MaxFrameRate.ToDouble())
                    .FirstOrDefault()
                    ?? throw new InvalidOperationException("Camera advertised no formats.");

                Console.WriteLine($"  → opening session at {format.Width}x{format.Height} {format.PixelFormat}");
                return await CameraSession.OpenAsync(deviceInfo, new CameraConfiguration(format), ct: ct)
                    .ConfigureAwait(false);
            };

        // onSessionEnded fires when the device disappears or the session
        // faults — the host disposes the session and waits for the next
        // device match.
        Func<CameraSession, Task> onSessionEnded = session =>
        {
            Console.WriteLine($"  ← session ended for {session.DeviceInfo.Name ?? "(unnamed)"}");
            return Task.CompletedTask;
        };

        await using var host = await DeviceSessionHost<CameraSession>.StartAsync(
            profile, createSession, onSessionEnded, ct: cts.Token).ConfigureAwait(false);

        // Subscribe to status changes for visibility. The host also raises
        // INotifyPropertyChanged for richer UI bindings.
        var lastStatusName = "";
        host.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(host.Status)) return;
            var label = DescribeStatus(host.Status);
            if (label == lastStatusName) return;
            lastStatusName = label;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {label}");
        };

        // Print the initial status, then idle until the deadline or Ctrl-C.
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {DescribeStatus(host.Status)}");

        try
        {
            await Task.Delay(Timeout.Infinite, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // expected
        }

        Console.WriteLine();
        Console.WriteLine("Shutting down host…");
        return 0;
    }

    private static string DescribeStatus(HostStatus<CameraSession> status) => status switch
    {
        DeviceAbsent<CameraSession>           => "Status: DeviceAbsent — waiting for a matching camera",
        SessionStarting<CameraSession> s      => $"Status: SessionStarting — {s.Device.Name ?? "(unnamed)"}",
        SessionActive<CameraSession> a        => $"Status: SessionActive — {a.Device.Name ?? "(unnamed)"}",
        SessionUnavailable<CameraSession> u   =>
            $"Status: SessionUnavailable — attempt {u.Attempt}" +
            (u.LastError is null ? "" : $", last error: {u.LastError.GetType().Name}: {u.LastError.Message}"),
        _ => $"Status: {status.GetType().Name}",
    };
}

using Periphery.Examples.Camera.Commands;

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    PrintUsage();
    return 0;
}

try
{
    return args[0] switch
    {
        "list"      => await ListCommand.RunAsync(args[1..]),
        "snapshot"  => await SnapshotCommand.RunAsync(args[1..]),
        "capture"   => await CaptureCommand.RunAsync(args[1..]),
        "controls"  => await ControlsCommand.RunAsync(args[1..]),
        "host"      => await HostCommand.RunAsync(args[1..]),
        _ => Unknown(args[0]),
    };
}
catch (OperationCanceledException)
{
    Console.WriteLine();
    Console.WriteLine("Cancelled.");
    return 130;
}

static int Unknown(string cmd)
{
    Console.Error.WriteLine($"Unknown command: {cmd}");
    Console.Error.WriteLine();
    PrintUsage();
    return 2;
}

static void PrintUsage()
{
    Console.WriteLine("""
        periphery-camera-example — showcase the Periphery.Camera API

        Usage:
          periphery-camera-example <command> [options]

        Commands:
          list                       Discover cameras attached to the system.
          snapshot [--device NAME]   Read formats and controls without keeping the device open.
          capture  [--device NAME] [--frames N] [--save DIR] [--format mjpeg|nv12]
                                     Open a session and capture N frames; optionally
                                     write MJPEG frames as .jpg files.
          controls [--device NAME] [--set KIND=VALUE] [--reset KIND]
                                     Read controls; optionally set or reset one.
          host     [--device NAME] [--seconds N]
                                     Run a DeviceSessionHost<CameraSession> for N
                                     seconds, printing status transitions on
                                     unplug/replug.

        Common options:
          --device NAME   Match the first device whose Name contains NAME (case-insensitive).
                          If omitted, the first discovered camera is used.

        Examples:
          periphery-camera-example list
          periphery-camera-example snapshot
          periphery-camera-example capture --frames 60 --save ./out
          periphery-camera-example controls --set Brightness=12
          periphery-camera-example host --seconds 30
        """);
}

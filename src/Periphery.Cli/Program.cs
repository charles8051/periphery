// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Reflection;
using Periphery.Cli.Commands;
using Spectre.Console.Cli;

var app = new CommandApp();
app.Configure(config =>
{
    config.SetApplicationName("periphery");
    config.SetApplicationVersion(
        Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
        ?? "unknown");

    // Errors go to STDERR, never stdout. The default handler renders through
    // AnsiConsole, i.e. stdout — so `periphery monitor layout --json > layout.json`
    // on a platform without a backend wrote the error message *into the JSON file*,
    // and the downstream parser failed on that instead of on the real cause.
    // Measured on an Ubuntu 24.04 box: 139 bytes on stdout, 0 on stderr.
    // A smoke check must be able to trust that stdout is data and stderr is
    // diagnostics. The non-zero exit code is preserved.
    config.SetExceptionHandler((ex, _) =>
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        // Full detail only when asked, so a normal failure stays one readable line
        // but a genuinely puzzling one is still diagnosable on a remote box.
        if (Environment.GetEnvironmentVariable("PERIPHERY_CLI_TRACE") == "1")
            Console.Error.WriteLine(ex);
        return -1;
    });

    config.AddBranch("devices", devices =>
    {
        devices.SetDescription("Inspect connected hardware devices.");
        devices.AddCommand<ListCommand>("list")
            .WithDescription("Print a snapshot of connected devices (table or JSON).")
            .WithExample("devices", "list")
            .WithExample("devices", "list", "--category", "Usb")
            .WithExample("devices", "list", "--json");
        devices.AddCommand<WatchCommand>("watch")
            .WithDescription("Stream device connect/disconnect events and (optionally) property changes.")
            .WithExample("devices", "watch")
            .WithExample("devices", "watch", "--category", "Bluetooth")
            .WithExample("devices", "watch", "--vid", "046D", "--pid", "C52B")
            .WithExample("devices", "watch", "--manufacturer", "Logitech")
            .WithExample("devices", "watch", "--category", "Battery", "--properties", "BatteryChargePercent,BatteryStatus");
        devices.AddCommand<DashboardCommand>("dashboard")
            .WithDescription("Launch a live terminal dashboard of connected devices.")
            .WithExample("devices", "dashboard");
        devices.AddCommand<ResetCommand>("reset")
            .WithDescription("Cycle a device's transport (ADR-0060): PnP disable/enable or USB port-cycle. Pair with `devices watch --verbose`.")
            .WithExample("devices", "reset", "USB\\VID_10C4&PID_8A7E\\6&...", "--list")
            .WithExample("devices", "reset", "USB\\VID_10C4&PID_8A7E\\6&...", "--strategy", "PnpDisableEnable", "--verbose")
            .WithExample("devices", "reset", "USB\\VID_10C4&PID_8A7E\\6&...", "--dry-run");
    });

    config.AddBranch("hid", hid =>
    {
        hid.SetDescription("Inspect and exercise HID devices directly (Periphery.Hid).");

        hid.AddBranch("feature", feature =>
        {
            feature.SetDescription("Send and receive HID feature reports (control-plane I/O).");
            feature.AddCommand<HidFeatureReadCommand>("read")
                .WithDescription("Read a feature report from a HID device.")
                .WithExample("hid", "feature", "read", "\"HID\\VID_0665&PID_5161\\...\"", "--report", "0", "--ascii");
            feature.AddCommand<HidFeatureWriteCommand>("write")
                .WithDescription("Send a feature report to a HID device.")
                .WithExample("hid", "feature", "write", "\"HID\\VID_0665&PID_5161\\...\"", "--report", "0", "--ascii", "Q1\\r");
        });

        hid.AddBranch("report", report =>
        {
            report.SetDescription("Send and receive HID input/output reports (data-plane I/O). " +
                                  "Use this when the device routes its request/response over input/output " +
                                  "reports rather than feature reports — common for vendor-defined HID " +
                                  "surfaces like Megatec-clone UPSs on Cypress 0665 silicon.");
            report.AddCommand<HidReportReadCommand>("read")
                .WithDescription("Read N input reports from a HID device, with a per-read timeout.")
                .WithExample("hid", "report", "read", "\"...\"", "--count", "8", "--timeout", "1500", "--ascii");
            report.AddCommand<HidReportWriteCommand>("write")
                .WithDescription("Send a single output report to a HID device. Padded to MaxOutputReportLength by default.")
                .WithExample("hid", "report", "write", "\"...\"", "--report", "0", "--ascii", "Q1\\r");
        });
    });

    config.AddBranch("battery", battery =>
    {
        battery.SetDescription("Battery-aware enumeration. Surfaces system batteries (laptop, ACPI) " +
                               "AND HID-class UPSs that get tagged via Periphery.Hid's codec-driven enrichment " +
                               "(ADR-0048). Combines DeviceTags.Battery + DeviceFilter.WithTag's Category-fallback.");
        battery.AddCommand<BatteryListCommand>("list")
            .WithDescription("List every device that exposes a battery surface.")
            .WithExample("battery", "list")
            .WithExample("battery", "list", "--json");
        battery.AddCommand<BatteryShowCommand>("show")
            .WithDescription("Run the HID battery enricher against one device and dump the snapshot.")
            .WithExample("battery", "show", "\"HID\\VID_0665&PID_5161\\...\"");
    });

    config.AddBranch("monitor", monitor =>
    {
        monitor.SetDescription("Monitor control (ADR-0058): DDC/CI VCP (brightness, power, input) " +
                               "and display-mode (resolution, orientation) planes, each present only " +
                               "where the hardware/OS offers it.");
        monitor.AddCommand<MonitorListCommand>("list")
            .WithDescription("List monitors with their control planes and live mode (transient handle per row).")
            .WithExample("monitor", "list");
        monitor.AddCommand<MonitorBrightnessCommand>("set-brightness")
            .WithDescription("Set DDC/CI brightness as a percent of the panel's maximum.")
            .WithExample("monitor", "set-brightness", "30")
            .WithExample("monitor", "set-brightness", "70", "--name", "DELL U2720Q");
        monitor.AddCommand<MonitorVcpCommand>("vcp")
            .WithDescription("Raw VCP get/set for any MCCS code.")
            .WithExample("monitor", "vcp", "get", "0x10")
            .WithExample("monitor", "vcp", "set", "0xD6", "0x04");
        monitor.AddCommand<MonitorModesCommand>("modes")
            .WithDescription("List the display modes the OS accepts for one monitor.")
            .WithExample("monitor", "modes");
        monitor.AddCommand<MonitorSetResolutionCommand>("set-resolution")
            .WithDescription("Set the display mode (persists by default; --no-persist for session-only).")
            .WithExample("monitor", "set-resolution", "720x1280@60")
            .WithExample("monitor", "set-resolution", "1920x1080", "--no-persist");
        monitor.AddCommand<MonitorLayoutCommand>("layout")
            .WithDescription("Whole-topology snapshot: current vs preferred mode, rotation, output technology, position, primary (ADR-0059).")
            .WithExample("monitor", "layout")
            .WithExample("monitor", "layout", "--json");
        monitor.AddCommand<MonitorSetPrimaryCommand>("set-primary")
            .WithDescription("Designate a monitor as primary (whole-topology translation; persists by default).")
            .WithExample("monitor", "set-primary", "--name", "DELL U2720Q");
        monitor.AddCommand<MonitorSetOrientationCommand>("set-orientation")
            .WithDescription("Rotate the output (width/height swap handled automatically).")
            .WithExample("monitor", "set-orientation", "portrait");
    });
});

return await app.RunAsync(args);

// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Periphery;
using Periphery.Bootloader;
using Periphery.Firmware;
using Periphery.Hid;

namespace Periphery.Bootloader.Efm8.Usb;

/// <summary>
/// The <see cref="IFirmwareProgrammer"/> shell for the SiLabs EFM8 factory USB-HID bootloader
/// (AN945). Wraps the pure <see cref="Efm8BootloaderUploader"/> record-replay core and the
/// <see cref="HidEfm8Transport"/>, exposing the platform flash contract so an EFM8 device routes
/// through the shared FlashAnything dispatcher like any other flasher (ADR-0061 / ADR-0063 slice 2).
/// </summary>
/// <remarks>
/// <para>
/// EFM8 firmware is a Kind-2 <b>packaged blob</b> — an AN945 boot-record stream consumed as-is — so
/// this programmer accepts a <see cref="FirmwareFormat.Efm8BootRecords"/>
/// <see cref="FirmwarePayload"/> and refuses anything else (the safety gate). The blob's final
/// run-application record resets the device into the freshly written app, so the leave is implicit
/// in the records: <see cref="FlashOptions.LeaveAfterFlash"/> cannot be selectively suppressed and
/// <see cref="LeaveAsync"/> is a no-op.
/// </para>
/// <para>
/// This is the imperative shell (ADR-0052): it owns the HID handle; the protocol core
/// (<see cref="Efm8Protocol"/>) and the replay loop are pure / link-agnostic beneath it.
/// </para>
/// </remarks>
public sealed class Efm8HidProgrammer : IFirmwareProgrammer
{
    private static readonly ImmutableArray<FirmwareFormat> s_acceptedFormats =
        ImmutableArray.Create(FirmwareFormat.Efm8BootRecords);

    // Static by-category logger over the shared Periphery sink (NullLogger unless the host wired
    // PeripheryLoggerFactory, e.g. the flasher's --log-file / -v), matching Efm8BootloaderUploader (#219).
    private static readonly ILogger _logger =
        PeripheryLoggerFactory.CreateLogger("Periphery.Bootloader.Efm8.Usb.Programmer");

    private readonly HidDevice? _device;   // null in unit tests (transport injected)
    private readonly IEfm8Transport _transport;

    private Efm8HidProgrammer(DeviceInfo device, HidDevice? hid, IEfm8Transport transport)
    {
        Device = device;
        _device = hid;
        _transport = transport;
    }

    /// <inheritdoc />
    public DeviceInfo Device { get; }

    /// <inheritdoc />
    public ImmutableArray<FirmwareFormat> AcceptedFormats => s_acceptedFormats;

    /// <summary>Test factory: drives the shell against a fake transport, no hardware.</summary>
    internal static Efm8HidProgrammer CreateForTest(DeviceInfo device, IEfm8Transport transport)
        => new(device, hid: null, transport);

    /// <summary>Opens the EFM8 HID bootloader device. It must already be in bootloader mode (<c>0x10C4:0xEAC9</c>).</summary>
    public static async Task<Efm8HidProgrammer> OpenAsync(DeviceInfo device, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        // Log the USB topology at open. Every EFM8 bootloader enumerates as the same 0x10C4:0xEAC9
        // shared id, so when two boards fail concurrently the only way to see whether they share a hub
        // or root port — the physical bus a current-collision rides — is this location/parent path.
        // The raw strings stay: the parse below is an addition to the evidence, not a replacement for
        // it (ADR-0079 D6, ADR-0073 D1). TryParse's own bool is not read — an unparsed value fails the
        // gates anyway, and "unavailable" is the only honest rendering of it. Reporting a path that is
        // not a port path as zero hubs is the failure D7 exists to prevent.
        //
        // The parsed rendering carries the hop vector, which is what makes two of these lines answer
        // the ROOT-PORT half of the question the comment above poses — a single open sees one device,
        // so the comparison itself can only happen between two lines. It renders "<unparsed>" rather
        // than anything a reader could mistake for a position.
        _ = PortPath.TryParse(device.LocationPath, out var portPath);
        var externalHubs = portPath.TryGetExternalHubCount(out var hubCount) ? hubCount.ToString() : "unavailable";
        var rootHub = portPath.TryGetIsRootHub(out var isRootHub) ? (isRootHub ? "yes" : "no") : "unavailable";
        _logger.LogInformation(
            "EFM8 open {Id}: VID/PID {Vid}:{Pid}, location '{Location}', parent '{Parent}', port {Port}; "
                + "parsed topology: {Parsed}, external hubs {ExternalHubs}, root hub {RootHub}.",
            device.Id,
            device.VendorId?.ToString() ?? "?",
            device.ProductId?.ToString() ?? "?",
            device.LocationPath ?? "?",
            device.ParentId?.ToString() ?? "?",
            device.PortNumber?.ToString() ?? "?",
            portPath,
            externalHubs,
            rootHub);
        var hid = await HidDevice.OpenAsync(device, ct).ConfigureAwait(false);
        try
        {
            return new Efm8HidProgrammer(device, hid, new HidEfm8Transport(hid));
        }
        catch
        {
            await hid.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public Task<DeviceIdentity> IdentifyAsync(CancellationToken ct = default)
        // The AN945 factory bootloader has no interactive identify exchange in the replay model — it
        // acknowledges records. Report the family and the HID output-report payload as the transfer size.
        => Task.FromResult(new DeviceIdentity(
            Family: "EFM8",
            Chip: null,
            BootloaderVersion: null,
            TransferSize: Efm8Protocol.OutputReportSize,
            Regions: ImmutableArray<MemoryRegion>.Empty,
            SupportedCommands: ImmutableArray<string>.Empty));

    /// <inheritdoc />
    public async Task<FlashResult> FlashAsync(
        FirmwarePayload payload, FlashOptions options, IProgress<FlashProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(options);

        // Safety gate: EFM8 replays a boot-record blob verbatim; it flashes nothing else.
        if (payload.Format != FirmwareFormat.Efm8BootRecords || payload.Kind != FirmwareKind.PackagedBlob)
            return FlashResult.Fail(
                $"EFM8 USB flashes {FirmwareFormat.Efm8BootRecords} boot records; '{payload.Format}' is not one.");

        var blob = payload.Blob;
        var sink = progress is null ? null : new Efm8ProgressAdapter(progress);
        try
        {
            // The act of calling FlashAsync is the erase-and-rewrite intent (the contract has no
            // separate confirmation), so the destructive-op guard is satisfied here.
            // replyTimeout / timeProvider default (5s per reply on the system clock): a stalled
            // bootloader now yields a reported failure instead of hanging the flash indefinitely.
            var upload = await Efm8BootloaderUploader.UploadAsync(
                _transport, blob, Efm8FlashConfirmation.ConfirmEraseAndReflash, sink, ct: ct).ConfigureAwait(false);

            if (!upload.Success)
                return FlashResult.Fail(upload.Describe());

            // Success: the run-app record already reset the device into the new app. Each record was
            // acknowledged by the bootloader, but there is no read-back, so this is not "verified".
            progress?.Report(new FlashProgress(FlashPhase.Done, upload.TotalBytes, upload.TotalBytes));
            return FlashResult.Ok(upload.TotalBytes, verified: false);
        }
        catch (Efm8BootFormatException ex)
        {
            return FlashResult.Fail(ex);
        }
        catch (Efm8BootloaderException ex)
        {
            return FlashResult.Fail(ex);
        }
    }

    /// <inheritdoc />
    /// <remarks>A no-op: the boot-record stream's final run-application record leaves the bootloader implicitly.</remarks>
    public Task LeaveAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_device is not null)
            await _device.DisposeAsync().ConfigureAwait(false);
    }

    // Maps the EFM8 per-record progress onto the platform's FlashProgress (the write phase).
    private sealed class Efm8ProgressAdapter(IProgress<FlashProgress> inner) : IProgress<Efm8UploadProgress>
    {
        public void Report(Efm8UploadProgress p)
            => inner.Report(new FlashProgress(FlashPhase.Writing, p.BytesSent, p.TotalBytes));
    }
}

// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Periphery.Bootloader;
using Periphery.Bootloader.Efm8.Usb;
using Periphery.Firmware;
using Periphery.Hid;

namespace Periphery.Treehopper.Firmware;

/// <summary>
/// Convenience wrapper that drives the full Treehopper firmware reflash sequence. Since ADR-0063 it
/// is a thin composition over the shared, watcher-driven
/// <see cref="BootloaderEntryOrchestrator"/>: a <see cref="TreehopperBootloaderEntry"/> reboots the
/// board into its USB-HID bootloader, the orchestration waits for the EFM8 bootloader to
/// re-enumerate and applies the safety gate, a flash callback replays a hex2boot-produced
/// boot-record file over the generic <see cref="Efm8BootloaderUploader"/>, and the orchestration
/// then optionally waits for the application to return.
/// </summary>
/// <remarks>
/// <para>
/// The <b>Treehopper Flasher</b> app (<c>Periphery.Treehopper.Flasher</c>, ADR-0063 slice 4) is an
/// alternative front-end that flashes a Treehopper over the <em>same</em>
/// <see cref="BootloaderEntryOrchestrator"/> through the FlashAnything platform. This type stays the
/// lightweight, in-process reflash API for callers that don't want the FlashAnything stack (e.g. the
/// Treehopper control app); both paths share the orchestration. (Removing it in favour of one path
/// is tracked but deferred.)
/// </para>
/// <para>
/// <b>Destructive.</b> This erases and rewrites the board's firmware. The entry point requires an
/// explicit <see cref="Efm8FlashConfirmation"/> argument so it cannot be invoked by accident.
/// </para>
/// <para>
/// <b>Input.</b> <see cref="ReflashFromFileAsync(TreehopperBoard, string, Efm8FlashConfirmation, TreehopperReflashOptions?, IProgress{Efm8UploadProgress}?, CancellationToken)"/>
/// takes a firmware file and infers the format from its extension, <em>verifying it against the
/// content</em> (brick-guard): a <c>.hex</c> Intel HEX image is converted to boot records in-process
/// (via <see cref="Efm8BootRecordGenerator"/>, no external <c>hex2boot</c>), and a
/// <c>.tfi</c>/<c>.efm8</c> boot-record stream is replayed as-is. The lower-level
/// <see cref="ReflashAsync(TreehopperBoard, Stream, Efm8FlashConfirmation, TreehopperReflashOptions?, IProgress{Efm8UploadProgress}?, CancellationToken)"/>
/// takes a boot-record stream directly and validates it parses BEFORE the board is rebooted. The
/// device-bricking failsafes (reset-vector-last, never-Lock) live in
/// <see cref="Efm8BootRecordGenerator"/>; this only replays records.
/// </para>
/// <para>
/// The two USB re-enumerations (app to bootloader, then bootloader back to app) each drop the
/// previous handle. The orchestration is push-driven off a <see cref="DeviceWatcher"/> rather than a
/// poll loop, and correlates the re-enumerated bootloader by debounce (the EFM8 HID bootloader is
/// the shared id <c>0x10C4:0xEAC9</c> for every EFM8 part, so there is no serial to match on).
/// </para>
/// </remarks>
public static class TreehopperFirmwareUpdate
{
    /// <summary>SiLabs EFM8 factory USB-HID bootloader Vendor ID (<c>0x10C4</c>).</summary>
    /// <remarks>Confirmed from <c>hidport.py:16-18</c> (<c>EFM8_LOADERS</c>).</remarks>
    public static readonly HardwareId BootloaderVid = new(0x10C4);

    /// <summary>SiLabs EFM8 factory USB-HID bootloader Product ID (<c>0xEAC9</c>).</summary>
    /// <remarks>Confirmed from <c>hidport.py:16-18</c> (<c>EFM8_LOADERS</c>).</remarks>
    public static readonly HardwareId BootloaderPid = new(0xEAC9);

    /// <summary>Treehopper application Vendor ID (<c>0x10C4</c>) — the device after reflash.</summary>
    public static readonly HardwareId ApplicationVid = TreehopperBoard.Vid;

    /// <summary>Treehopper application Product ID (<c>0x8A7E</c>) — the device after reflash.</summary>
    public static readonly HardwareId ApplicationPid = TreehopperBoard.Pid;

    /// <summary>
    /// Reflashes the <paramref name="board"/> from a boot-record <paramref name="bootRecords"/>
    /// stream. <b>Takes ownership of and disposes <paramref name="board"/></b> — the board handle is
    /// dead the moment the device enters the bootloader, so the caller must not use it after this
    /// call.
    /// </summary>
    /// <param name="board">An open board. Disposed by this method.</param>
    /// <param name="bootRecords">
    /// A hex2boot-produced <c>.efm8</c>/<c>.tfi</c> stream (e.g. <c>File.OpenRead(path)</c>). Read
    /// fully into memory before the board reboots.
    /// </param>
    /// <param name="confirmation">
    /// Must be <see cref="Efm8FlashConfirmation.ConfirmEraseAndReflash"/>.
    /// </param>
    /// <param name="options">Timeouts / wait-for-app tunables, or <c>null</c> for defaults.</param>
    /// <param name="progress">Optional per-record progress sink.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="System.ArgumentException">
    /// <paramref name="confirmation"/> is not
    /// <see cref="Efm8FlashConfirmation.ConfirmEraseAndReflash"/>.
    /// </exception>
    /// <exception cref="BootloaderEntryException">
    /// The bootloader did not re-enumerate within the timeout, or the safety gate refused a device
    /// that was not the expected bootloader VID/PID.
    /// </exception>
    /// <exception cref="Efm8BootFormatException">The stream is not a well-formed boot-record file.</exception>
    public static async Task<TreehopperReflashResult> ReflashAsync(
        TreehopperBoard board,
        Stream bootRecords,
        Efm8FlashConfirmation confirmation,
        TreehopperReflashOptions? options = null,
        IProgress<Efm8UploadProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(board);

        // Read + brick-guard while the board is still open and the app is running (a wrong file is
        // rejected before the board is ever rebooted).
        var (imageBytes, opts) = await PrepareAsync(bootRecords, confirmation, options, ct).ConfigureAwait(false);

        // Hand off the board's identity and release the caller's handle: the orchestration re-opens
        // the board from this snapshot in TreehopperBootloaderEntry.EnterAsync to send the reboot.
        var deviceInfo = board.DeviceInfo;
        await board.DisposeAsync().ConfigureAwait(false);

        return await RunReflashAsync(deviceInfo, imageBytes, confirmation, opts, progress, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens the board identified by <paramref name="deviceInfo"/> and reflashes it. Sugar over
    /// <see cref="ReflashAsync(TreehopperBoard, Stream, Efm8FlashConfirmation, TreehopperReflashOptions?, IProgress{Efm8UploadProgress}?, CancellationToken)"/>.
    /// </summary>
    public static async Task<TreehopperReflashResult> ReflashAsync(
        DeviceInfo deviceInfo,
        Stream bootRecords,
        Efm8FlashConfirmation confirmation,
        TreehopperReflashOptions? options = null,
        IProgress<Efm8UploadProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(deviceInfo);

        var (imageBytes, opts) = await PrepareAsync(bootRecords, confirmation, options, ct).ConfigureAwait(false);
        return await RunReflashAsync(deviceInfo, imageBytes, confirmation, opts, progress, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reflashes <paramref name="board"/> from a firmware file at <paramref name="firmwarePath"/>.
    /// The format is inferred from the extension and <b>verified against the file content</b>
    /// (<see cref="Efm8FirmwareImage"/>): a <c>.hex</c> Intel HEX image is converted to boot records
    /// in-process, a <c>.tfi</c>/<c>.efm8</c> boot-record stream is replayed as-is, and a file whose
    /// content does not match its extension is refused. All of that runs on the file bytes
    /// <b>before</b> the board is touched, so a wrong file can never reach the device. <b>Takes
    /// ownership of and disposes <paramref name="board"/></b> once it proceeds (see the boot-record
    /// overload).
    /// </summary>
    /// <exception cref="Efm8BootFormatException">
    /// The extension is unrecognized, the content does not match the extension, or the file is
    /// malformed — thrown before the board is rebooted.
    /// </exception>
    public static async Task<TreehopperReflashResult> ReflashFromFileAsync(
        TreehopperBoard board,
        string firmwarePath,
        Efm8FlashConfirmation confirmation,
        TreehopperReflashOptions? options = null,
        IProgress<Efm8UploadProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentException.ThrowIfNullOrEmpty(firmwarePath);

        byte[] records = await ReadAndResolveAsync(firmwarePath, ct).ConfigureAwait(false);
        using var stream = new MemoryStream(records, writable: false);
        return await ReflashAsync(board, stream, confirmation, options, progress, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens the board identified by <paramref name="deviceInfo"/> and reflashes it from
    /// <paramref name="firmwarePath"/>. The file is read and validated <b>before</b> the board is
    /// opened, so a bad file fails without touching any device.
    /// </summary>
    public static async Task<TreehopperReflashResult> ReflashFromFileAsync(
        DeviceInfo deviceInfo,
        string firmwarePath,
        Efm8FlashConfirmation confirmation,
        TreehopperReflashOptions? options = null,
        IProgress<Efm8UploadProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(deviceInfo);
        ArgumentException.ThrowIfNullOrEmpty(firmwarePath);

        byte[] records = await ReadAndResolveAsync(firmwarePath, ct).ConfigureAwait(false);
        using var stream = new MemoryStream(records, writable: false);
        return await ReflashAsync(deviceInfo, stream, confirmation, options, progress, ct).ConfigureAwait(false);
    }

    // Validate the confirmation, read the stream fully, and brick-guard (a malformed stream is
    // rejected here, before any device IO). The board, if any, is still open and running its app at
    // this point — a refusal never reaches the metal.
    private static async Task<(byte[] ImageBytes, TreehopperReflashOptions Options)> PrepareAsync(
        Stream bootRecords, Efm8FlashConfirmation confirmation, TreehopperReflashOptions? options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bootRecords);
        // Re-checked inside UploadAsync, but fail fast before we reboot the board.
        if (confirmation != Efm8FlashConfirmation.ConfirmEraseAndReflash)
            throw new ArgumentException(
                "A Treehopper reflash erases and rewrites firmware. Pass "
                + "Efm8FlashConfirmation.ConfirmEraseAndReflash to proceed.",
                nameof(confirmation));

        options ??= new TreehopperReflashOptions();

        // Read the whole image up front — replay-only, and the source handle may be unrelated to the
        // device, but we must hold the bytes across the reboot.
        var imageBytes = await ReadAllAsync(bootRecords, ct).ConfigureAwait(false);

        // Brick-guard: fail fast on a non-boot-record stream BEFORE we reboot the board. Same total
        // parse UploadAsync runs later, but doing it here means a wrong file (e.g. raw Intel HEX) is
        // rejected while the application is still safely running.
        Efm8Protocol.ParseRecords(imageBytes);

        return (imageBytes, options);
    }

    // Compose the Treehopper entry + the shared orchestration + an EFM8-HID flash callback. The
    // hand-rolled reboot/poll/gate/poll sequence is now the orchestration's job (ADR-0063 slice 1):
    // enter -> wait-for-bootloader (debounce) -> safety gate -> flash -> optional wait-for-app.
    private static async Task<TreehopperReflashResult> RunReflashAsync(
        DeviceInfo deviceInfo,
        byte[] imageBytes,
        Efm8FlashConfirmation confirmation,
        TreehopperReflashOptions options,
        IProgress<Efm8UploadProgress>? progress,
        CancellationToken ct)
    {
        var entry = new TreehopperBootloaderEntry();
        var entryOptions = new BootloaderEntryOptions
        {
            BootloaderTimeout = options.EffectiveBootloaderTimeout,
            // After the final RunApp record resets the device, wait for the application identity to
            // re-appear (when requested) and report it.
            ApplicationFilter = options.WaitForApplication
                ? new DeviceFilter().WithUsbId(ApplicationVid, ApplicationPid)
                : null,
            ApplicationTimeout = options.EffectiveApplicationTimeout,
        };

        var outcome = await BootloaderEntryOrchestrator.RunAsync(
            entry,
            deviceInfo,
            flash: (bootloaderDevice, token) =>
                FlashOverHidAsync(bootloaderDevice, imageBytes, confirmation, progress, token),
            entryOptions,
            // Only wait for the app to return when the upload actually succeeded.
            flashSucceeded: static result => result.Success,
            ct: ct).ConfigureAwait(false);

        return new TreehopperReflashResult(outcome.FlashResult, outcome.ApplicationReturned);
    }

    /// <summary>
    /// Read-only check: does the board identified by <paramref name="deviceInfo"/>'s <b>current</b>
    /// flash content match the image at <paramref name="firmwarePath"/>? Reboots into the bootloader
    /// exactly as a reflash would, replays a verify-only stream
    /// (<see cref="Efm8BootRecordGenerator.VerifyOnly"/> — a Setup record and one Verify record per
    /// region, no RunApp), then <b>always</b> leaves the bootloader via a separate transfer
    /// regardless of whether the verify matched. <b>No Erase or Write record exists in the verify
    /// stream</b>, so this cannot modify the board's firmware no matter the outcome.
    /// </summary>
    /// <param name="deviceInfo">The application-mode device to check.</param>
    /// <param name="firmwarePath">
    /// An Intel HEX (<c>.hex</c>) image to compare against. Unlike <see cref="ReflashFromFileAsync(DeviceInfo,string,Efm8FlashConfirmation,TreehopperReflashOptions?,System.IProgress{Efm8UploadProgress}?,System.Threading.CancellationToken)"/>,
    /// only <c>.hex</c> is accepted — a verify-only stream is built directly from the parsed image
    /// (<see cref="IntelHexImage"/>), not from an already-boot-recorded <c>.tfi</c>/<c>.efm8</c>
    /// stream, which no longer carries the per-region CRCs in a form this can recompute a check from.
    /// </param>
    /// <param name="options">Timeouts / wait-for-app tunables, or <c>null</c> for defaults.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="Efm8BootFormatException">The file's extension is not <c>.hex</c>, or its content is not valid Intel HEX.</exception>
    /// <exception cref="ArgumentException"><paramref name="firmwarePath"/> parses to an empty image, which would trivially "match" without checking any flash content.</exception>
    /// <exception cref="BootloaderEntryException">
    /// The bootloader did not re-enumerate within the timeout, or the safety gate refused a device
    /// that was not the expected bootloader VID/PID.
    /// </exception>
    public static async Task<TreehopperVerifyResult> VerifyFromFileAsync(
        DeviceInfo deviceInfo,
        string firmwarePath,
        TreehopperReflashOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(deviceInfo);
        ArgumentException.ThrowIfNullOrEmpty(firmwarePath);

        if (Efm8FirmwareImage.FormatFromFileName(firmwarePath) != Efm8FirmwareFormat.IntelHex)
            throw new Efm8BootFormatException(
                $"Verify needs an Intel HEX (.hex) image to compute a check from; " +
                $"'{firmwarePath}' is not one.");

        string hexText = await File.ReadAllTextAsync(firmwarePath, ct).ConfigureAwait(false);
        var image = IntelHexImage.Parse(hexText);
        byte[] verifyRecords = Efm8BootRecordGenerator.VerifyOnly(image, Efm8BootOptions.Ub1);

        var opts = options ?? new TreehopperReflashOptions();
        var entry = new TreehopperBootloaderEntry();
        var entryOptions = new BootloaderEntryOptions
        {
            BootloaderTimeout = opts.EffectiveBootloaderTimeout,
            ApplicationFilter = opts.WaitForApplication
                ? new DeviceFilter().WithUsbId(ApplicationVid, ApplicationPid)
                : null,
            ApplicationTimeout = opts.EffectiveApplicationTimeout,
        };

        var outcome = await BootloaderEntryOrchestrator.RunAsync(
            entry,
            deviceInfo,
            flash: (bootloaderDevice, token) => Efm8VerifyOperation.RunAsync(bootloaderDevice, verifyRecords, token),
            entryOptions,
            // Wait for the app regardless of the verify's own outcome: Efm8VerifyOperation.RunAsync
            // always sends the leave-transfer itself, in a finally, so the board is back in app mode
            // whether or not the Verify record ACK'd.
            flashSucceeded: static _ => true,
            ct: ct).ConfigureAwait(false);

        return new TreehopperVerifyResult(outcome.FlashResult, outcome.ApplicationReturned);
    }

    // The flash callback: open the bootloader HID device, replay the records over the generic EFM8
    // uploader, and dispose the handle (so the device can re-enumerate into the freshly written app).
    private static async Task<Efm8UploadResult> FlashOverHidAsync(
        DeviceInfo bootloaderDevice,
        byte[] imageBytes,
        Efm8FlashConfirmation confirmation,
        IProgress<Efm8UploadProgress>? progress,
        CancellationToken ct)
    {
        var hid = await HidDevice.OpenAsync(bootloaderDevice, ct).ConfigureAwait(false);
        try
        {
            var transport = new HidEfm8Transport(hid);
            // Default reply timeout / system clock; a stalled bootloader now fails instead of hanging.
            return await Efm8BootloaderUploader.UploadAsync(
                transport, imageBytes, confirmation, progress, ct: ct).ConfigureAwait(false);
        }
        finally
        {
            await hid.DisposeAsync().ConfigureAwait(false);
        }
    }

    // Read a firmware file and resolve it to a boot-record stream (Treehopper = EFM8UB1), inferring
    // the format from the extension and verifying it against the content. Pure verification on the
    // bytes — no device IO — so a refusal never touches the board.
    private static async Task<byte[]> ReadAndResolveAsync(string firmwarePath, CancellationToken ct)
    {
        byte[] fileBytes = await File.ReadAllBytesAsync(firmwarePath, ct).ConfigureAwait(false);
        return Efm8FirmwareImage.ToBootRecords(
            fileBytes, Path.GetFileName(firmwarePath), Efm8BootOptions.Ub1);
    }

    private static async Task<byte[]> ReadAllAsync(Stream source, CancellationToken ct)
    {
        if (source is MemoryStream ms && ms.TryGetBuffer(out var seg))
        {
            // Copy out the exact written length — TryGetBuffer's array may be oversized.
            var exact = new byte[seg.Count];
            Array.Copy(seg.Array!, seg.Offset, exact, 0, seg.Count);
            return exact;
        }

        using var buffer = new MemoryStream();
        await source.CopyToAsync(buffer, ct).ConfigureAwait(false);
        return buffer.ToArray();
    }
}

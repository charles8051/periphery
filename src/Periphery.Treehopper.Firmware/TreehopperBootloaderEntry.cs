// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Threading;
using System.Threading.Tasks;
using Periphery.Bootloader;
using Periphery.Bootloader.Efm8.Usb;
using Periphery.Firmware;

namespace Periphery.Treehopper.Firmware;

/// <summary>
/// The Treehopper half of the app-to-bootloader mode switch (ADR-0063 DEC-002): recognises a
/// Treehopper running its application firmware and reboots it into the SiLabs EFM8 USB-HID
/// bootloader (<c>0x10C4:0xEAC9</c>), where the generic EFM8 flasher takes over. The whole
/// device-specific addition is this thin wrapper over the wake command that already exists
/// (<see cref="TreehopperBoard.RebootIntoBootloaderAsync"/>); the reboot/wait/gate/flash spine is
/// the shared <see cref="BootloaderEntryOrchestrator"/>.
/// </summary>
/// <remarks>
/// Implements only the mode switch — it opens the board, sends the wake command, and disposes. It
/// never polls, gates, or flashes; the orchestrator owns the wait, the
/// <see cref="ExpectedBootloader"/> safety gate, and the flash.
/// </remarks>
public sealed class TreehopperBootloaderEntry : IBootloaderEntry
{
    // The EFM8 HID bootloader id is a constant identity; build the filter once and share it (the
    // orchestrator only reads it — to seed the watcher and as the safety gate).
    //
    // OfCategory(Hid) + WithBusType(Hid) matters, not just the USB id: a USB-HID device enumerates
    // as TWO PnP nodes sharing the same VID/PID — the raw USB device node and its HID child
    // interface — and only the HID node is something HidDevice.OpenAsync can open (mirrors
    // Efm8UsbBootloaderProvider.CanHandle's identical guard, with the identical rationale). Inside
    // FlashAnythingService this never bit: its MultiDeviceTracker pre-filters through
    // BootloaderRegistry.Match, which already excludes the raw USB node before the orchestrator's
    // FirstAppearance correlation ever runs. TreehopperFirmwareUpdate's standalone orchestration
    // (ReflashAsync, VerifyFromFileAsync) has no such pre-filter, so a VID/PID-only ExpectedBootloader
    // let FirstAppearance grab whichever node the OS reports first — deterministically the unopenable
    // raw USB node on at least one bench machine, stranding the board in the bootloader on open
    // (periphery#247).
    private static readonly DeviceFilter s_expectedBootloader = new DeviceFilter()
        .WithUsbId(TreehopperFirmwareUpdate.BootloaderVid, TreehopperFirmwareUpdate.BootloaderPid)
        .OfCategory(DeviceCategory.Hid)
        .WithBusType(BusType.HID);

    /// <inheritdoc/>
    public string Name => "Treehopper";

    /// <inheritdoc/>
    public bool CanEnter(DeviceInfo applicationDevice)
    {
        ArgumentNullException.ThrowIfNull(applicationDevice);
        return applicationDevice.VendorId == TreehopperBoard.Vid
            && applicationDevice.ProductId == TreehopperBoard.Pid;
    }

    /// <inheritdoc/>
    public DeviceFilter ExpectedBootloader => s_expectedBootloader;

    /// <inheritdoc/>
    public async Task EnterAsync(DeviceInfo applicationDevice, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(applicationDevice);
        await using var board = await TreehopperBoard.OpenAsync(applicationDevice, ct).ConfigureAwait(false);
        await board.RebootIntoBootloaderAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Treehopper's flasher (<c>Efm8HidProgrammer</c>) has no in-session read-back — its own embedded
    /// Verify record can ACK a write the bootloader did not actually commit (periphery#246). This
    /// gives <see cref="BootloaderEntryOrchestrator.RunWithVerificationAsync{TResult}"/> a genuinely
    /// independent, later-session check to catch that.
    /// </remarks>
    public bool CanVerify => true;

    /// <inheritdoc/>
    public async Task<bool> VerifyAsync(DeviceInfo bootloaderDevice, FirmwarePayload payload, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bootloaderDevice);
        ArgumentNullException.ThrowIfNull(payload);
        // Reconstructed from the exact blob just flashed (not the original source image, which the
        // caller may not even have at this layer) - see VerifyOnlyFromBlob's remarks for why this
        // rebuilds the true final image rather than replaying the blob's own embedded Verify record.
        byte[] verifyRecords = Efm8BootRecordGenerator.VerifyOnlyFromBlob(payload.Blob, Efm8BootOptions.Ub1);
        var result = await Efm8VerifyOperation.RunAsync(bootloaderDevice, verifyRecords, ct).ConfigureAwait(false);
        return result.Success;
    }
}

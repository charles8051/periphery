using System;
using Periphery.Usb.Linux;
using Periphery.Usb.Windows;
using Xunit;

namespace Periphery.Usb.Tests;

/// <summary>
/// Transfer-fault classification (#260).
/// <para>
/// A Treehopper board that left the USB bus mid-transfer used to surface as a generic
/// <see cref="UsbTransferException"/> whose message hedged three ways — "may have been
/// disconnected, the endpoint stalled, or the transfer was cancelled" — and an investigation
/// spent an hour on a firmware hypothesis the fault code had already excluded. The backends
/// themselves need a real device to test; this judgement does not, which is the point of
/// having the judgement live in a pure static on each backend.
/// </para>
/// </summary>
// Both backends carry a platform attribute for their interop; everything called here is a
// pure static that touches none of it, so the classification stays testable on any OS — which
// is the whole reason it is separable from the code that needs a device.
#pragma warning disable CA1416
public class TransferFaultClassificationTests
{
    private const string Device = @"\\?\usb#vid_10c4&pid_8a7e#a7ds6cd";

    // ── Win32: the unambiguous removals ───────────────────────────────

    [Theory]
    [InlineData(433)]   // ERROR_NO_SUCH_DEVICE
    [InlineData(1167)]  // ERROR_DEVICE_NOT_CONNECTED
    public void Win32_RemovalCodes_AreTypedAsARemoval(int error)
    {
        var ex = WinUsbBackend.ClassifyTransferError(error, "bulk read", "endpoint=0x81", Device);

        var removed = Assert.IsType<UsbDeviceRemovedException>(ex);
        Assert.Equal(Device, removed.DeviceId);
        Assert.Contains("left the USB bus", removed.Message);
    }

    [Theory]
    [InlineData(2)]  // ERROR_FILE_NOT_FOUND
    [InlineData(3)]  // ERROR_PATH_NOT_FOUND
    public void Win32_PipeNotFound_IsNotARemoval(int error)
    {
        // Same code, different meaning by call context. At open these come from CreateFile on
        // a device path and do mean "not there" — MapOpenError still reads them that way. On a
        // transfer they come from WinUsb_ReadPipe / WinUsb_WritePipe and mean the PIPE is not
        // found: an endpoint address that is not on the claimed interface. Calling that a
        // removal would send a caller off to wait for a re-enumeration that is never coming
        // (#272 review turn 5).
        var ex = WinUsbBackend.ClassifyTransferError(error, "bulk read", "endpoint=0x85", Device);

        Assert.IsType<UsbTransferException>(ex);
        Assert.IsNotType<UsbDeviceRemovedException>(ex);
        Assert.Contains("no pipe with that address", ex.Message);
        Assert.Contains("not evidence that the device left the bus", ex.Message);
    }

    // ── Win32: the codes that genuinely cannot be resolved ────────────

    [Theory]
    [InlineData(22)]  // ERROR_BAD_COMMAND  — the write-side code in #260
    [InlineData(31)]  // ERROR_GEN_FAILURE  — the read-side code in #260
    public void Win32_AmbiguousCodes_AreNotClaimedAsARemoval(int error)
    {
        // These mean "the device is not servicing the endpoint", which a surprise removal and
        // a stalled pipe produce alike. Guessing "removed" here would trade one misleading
        // message for another, so the ambiguity is reported instead of resolved.
        var ex = WinUsbBackend.ClassifyTransferError(error, "bulk read", "endpoint=0x81", Device);

        Assert.IsType<UsbTransferException>(ex);           // exact type: NOT the removal subtype
        Assert.IsNotType<UsbDeviceRemovedException>(ex);
        Assert.Contains("stopped servicing the endpoint", ex.Message);
        Assert.Contains("surprise removal", ex.Message);
        Assert.Contains("stalled pipe", ex.Message);
    }

    [Fact]
    public void Win32_AmbiguousCodes_AreNamedAsASet()
    {
        // Which codes cannot be resolved from the code alone is the substantive claim this
        // class makes, so it is exposed rather than buried in a switch arm. How to tell a
        // removal from a stall is host-diagnostic workflow and lives in the remarks on this
        // predicate, not in the exception message a consumer surfaces (#272 review turn 1).
        Assert.True(WinUsbBackend.IsAmbiguousTransferError(22));
        Assert.True(WinUsbBackend.IsAmbiguousTransferError(31));

        Assert.False(WinUsbBackend.IsAmbiguousTransferError(1167));
        Assert.False(WinUsbBackend.IsAmbiguousTransferError(2));
        Assert.False(WinUsbBackend.IsAmbiguousTransferError(9999));
    }

    [Fact]
    public void Win32_AmbiguousMessages_CarryTheClassificationButNotAHostWorkflow()
    {
        var ex = WinUsbBackend.ClassifyTransferError(31, "bulk read", "endpoint=0x81", Device);

        Assert.Contains("stopped servicing the endpoint", ex.Message);
        Assert.DoesNotContain("DEVPKEY", ex.Message);
        Assert.DoesNotContain("Kernel-PnP", ex.Message);
    }

    [Fact]
    public void Win32_NamesTheCodeSymbolically_NotJustItsNumber()
    {
        var ex = WinUsbBackend.ClassifyTransferError(31, "bulk read", "endpoint=0x81", Device);

        Assert.Contains("ERROR_GEN_FAILURE (31)", ex.Message);
        Assert.Contains("endpoint=0x81", ex.Message);
        Assert.Contains("bulk read", ex.Message);
    }

    [Fact]
    public void Win32_NoLongerClaimsTheTransferMayHaveBeenCancelled()
    {
        // A regression pin, not a style assertion. The old message offered cancellation as one
        // of three possibilities, and it was never one: the completion callback routes
        // ERROR_OPERATION_ABORTED to TrySetCanceled and never asks for a fault at all. A third
        // of the hedge was not merely vague, it was false.
        foreach (int error in new[] { 2, 3, 22, 31, 433, 1167, 9999 })
        {
            var ex = WinUsbBackend.ClassifyTransferError(error, "bulk write", "endpoint=0x02", Device);
            Assert.DoesNotContain("cancel", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Win32_UnknownCode_StaysAGenericTransferFailure()
    {
        var ex = WinUsbBackend.ClassifyTransferError(9999, "bulk write", "endpoint=0x02", Device);

        Assert.IsType<UsbTransferException>(ex);
        Assert.Contains("9999", ex.Message);
    }

    // ── The split is deliberate ───────────────────────────────────────

    [Fact]
    public void ARemovalIsStillCaughtAsATransferFailure()
    {
        // A removal SPECIALISES UsbTransferException rather than sitting beside it: it really
        // is one way a transfer fails, so a caller handling transfer failures generally keeps
        // catching unplugs, and one that wants to treat them differently catches the derived
        // type first. Specialisation adds information without taking any away
        // (#272 review turn 1).
        var removed = WinUsbBackend.ClassifyTransferError(1167, "bulk read", "endpoint=0x81", Device);

        Assert.IsType<UsbDeviceRemovedException>(removed);
        Assert.IsAssignableFrom<UsbTransferException>(removed);
    }

    // ── libusb: no ambiguity to resolve ───────────────────────────────

    [Fact]
    public void LibUsb_NoDevice_IsTypedAsARemoval()
    {
        var ex = LibUsbBackend.ClassifyTransferStatus(
            LibUsbInterop.LIBUSB_TRANSFER_NO_DEVICE, 0x81, Device);

        var removed = Assert.IsType<UsbDeviceRemovedException>(ex);
        Assert.Contains("left the USB bus", removed.Message);
        Assert.Contains("0x81", removed.Message);
    }

    [Fact]
    public void LibUsb_Stall_StaysATransferFailure_AndSaysTheDeviceIsStillThere()
    {
        // libusb reports removal and stall separately, so unlike Win32 this one is knowable —
        // and saying so is the useful half.
        var ex = LibUsbBackend.ClassifyTransferStatus(
            LibUsbInterop.LIBUSB_TRANSFER_STALL, 0x02, Device);

        Assert.IsType<UsbTransferException>(ex);
        Assert.Contains("still on the bus", ex.Message);
    }

    [Fact]
    public void LibUsb_NoDevice_MeansNotFoundBeforeTheHandleExists_AndRemovedAfter()
    {
        // MapError serves the OPEN path (libusb_wrap_sys_device, the descriptor reads,
        // ReadConfiguration) as well as post-open calls. Mapping every NO_DEVICE to a removal
        // reported "we had this device and it left" for one that was never opened, which is
        // exactly the distinction this PR exists to draw (#272 review turn 1).
        var atOpen = LibUsbBackend.MapError(
            LibUsbInterop.LIBUSB_ERROR_NO_DEVICE, Device, "libusb_wrap_sys_device failed");
        var afterOpen = LibUsbBackend.MapError(
            LibUsbInterop.LIBUSB_ERROR_NO_DEVICE, Device, "a transfer on endpoint 0x02",
            afterOpen: true);

        Assert.IsType<UsbDeviceNotFoundException>(atOpen);
        Assert.IsType<UsbDeviceRemovedException>(afterOpen);
    }

    [Fact]
    public void LibUsb_Overflow_StaysATransferFailure()
    {
        var ex = LibUsbBackend.ClassifyTransferStatus(
            LibUsbInterop.LIBUSB_TRANSFER_OVERFLOW, 0x81, Device);

        Assert.IsType<UsbTransferException>(ex);
        Assert.Contains("overflow", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

}
#pragma warning restore CA1416

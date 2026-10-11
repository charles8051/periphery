namespace Periphery.Usb.Browser.Tests;

/// <summary>The outcome table of ADR-0092 D6.</summary>
public class WebUsbOutcomesTests
{
    private const string Id = "webusb:10C4:8A7E:AB12CD";

    private static Exception Reject(string name, WebUsbPhase phase, byte endpoint = 0x81)
        => WebUsbOutcomes.FromRejection(new WebUsbRejection(name, "browser message"), phase, endpoint, Id);

    // ── Rejections during a transfer ───────────────────────────────────

    [Fact]
    public void A_disconnect_or_revocation_during_a_transfer_is_a_removal()
    {
        var ex = Reject("NotFoundError", WebUsbPhase.Transfer);

        var removed = Assert.IsType<UsbDeviceRemovedException>(ex);
        Assert.Equal(Id, removed.DeviceId);
    }

    [Theory]
    [InlineData("NetworkError")]
    [InlineData("TimeoutError")]
    public void A_transfer_error_is_a_transfer_fault(string name)
    {
        Assert.IsType<UsbTransferException>(Reject(name, WebUsbPhase.Transfer));
    }

    [Theory]
    [InlineData("InvalidStateError")]
    [InlineData("SecurityError")]
    [InlineData("DataError")]
    [InlineData("IndexSizeError")]
    [InlineData("SomethingNew")]
    public void A_refused_transfer_is_a_plain_usb_exception(string name)
    {
        Assert.IsType<UsbException>(Reject(name, WebUsbPhase.Transfer));
    }

    [Theory]
    [InlineData(nameof(WebUsbPhase.Open))]
    [InlineData(nameof(WebUsbPhase.Claim))]
    [InlineData(nameof(WebUsbPhase.Transfer))]
    public void An_abort_from_the_backends_own_close_is_disposal(string phase)
    {
        var ex = Assert.IsType<ObjectDisposedException>(Reject("AbortError", Enum.Parse<WebUsbPhase>(phase)));

        Assert.Equal(nameof(UsbDevice), ex.ObjectName);
    }

    // ── Rejections while opening ───────────────────────────────────────

    [Theory]
    [InlineData(nameof(WebUsbPhase.Open))]
    [InlineData(nameof(WebUsbPhase.Claim))]
    public void A_device_gone_before_it_opened_is_not_found(string phase)
    {
        Assert.IsType<UsbDeviceNotFoundException>(Reject("NotFoundError", Enum.Parse<WebUsbPhase>(phase)));
    }

    [Fact]
    public void Access_denied_at_open_is_access_denied()
    {
        Assert.IsType<UsbAccessDeniedException>(Reject("SecurityError", WebUsbPhase.Open));
    }

    [Fact]
    public void A_protected_interface_class_at_claim_is_access_denied()
    {
        var ex = Reject("SecurityError", WebUsbPhase.Claim);

        Assert.IsType<UsbAccessDeniedException>(ex);
        Assert.Contains("HID", ex.Message);
    }

    [Fact]
    public void An_interface_held_by_another_process_is_access_denied_and_says_so()
    {
        var ex = Reject("NetworkError", WebUsbPhase.Claim);

        Assert.IsType<UsbAccessDeniedException>(ex);
        Assert.Contains("Another process", ex.Message);
    }

    [Theory]
    [InlineData(nameof(WebUsbPhase.Open), "NetworkError")]
    [InlineData(nameof(WebUsbPhase.Open), "InvalidStateError")]
    [InlineData(nameof(WebUsbPhase.Claim), "InvalidStateError")]
    public void Other_open_failures_are_a_plain_usb_exception(string phase, string name)
    {
        Assert.IsType<UsbException>(Reject(name, Enum.Parse<WebUsbPhase>(phase)));
    }

    [Fact]
    public void The_browsers_name_and_message_travel_as_the_inner_exception()
    {
        var ex = WebUsbOutcomes.FromRejection(
            new WebUsbRejection("NotFoundError", "The device was disconnected."), WebUsbPhase.Transfer, 0x02, Id);

        Assert.Equal("WebUSB NotFoundError: The device was disconnected.", ex.InnerException?.Message);
        Assert.Contains("0x02", ex.Message);
    }

    // ── Resolved statuses ──────────────────────────────────────────────

    [Fact]
    public void Ok_is_success()
    {
        var verdict = WebUsbOutcomes.FromStatus("ok", 0x81, Id);

        Assert.True(verdict.IsOk);
        Assert.False(verdict.ClearHaltFirst);
    }

    [Theory]
    [InlineData((byte)0x81)]
    [InlineData((byte)0x02)]
    public void A_stall_on_a_bulk_endpoint_is_cleared_and_then_a_transfer_fault(byte endpoint)
    {
        var verdict = WebUsbOutcomes.FromStatus("stall", endpoint, Id);

        Assert.IsType<UsbTransferException>(verdict.Error);
        Assert.True(verdict.ClearHaltFirst);
    }

    [Theory]
    [InlineData((byte)0x00)]
    [InlineData((byte)0x80)]
    public void A_stall_on_endpoint_0_is_a_transfer_fault_with_no_clear_halt(byte endpoint)
    {
        var verdict = WebUsbOutcomes.FromStatus("stall", endpoint, Id);

        Assert.IsType<UsbTransferException>(verdict.Error);
        Assert.False(verdict.ClearHaltFirst);
    }

    [Fact]
    public void Babble_is_a_transfer_fault()
    {
        var verdict = WebUsbOutcomes.FromStatus("babble", 0x81, Id);

        Assert.IsType<UsbTransferException>(verdict.Error);
        Assert.False(verdict.ClearHaltFirst);
    }

    [Fact]
    public void An_unknown_status_is_a_transfer_fault_not_success()
    {
        var verdict = WebUsbOutcomes.FromStatus("weird", 0x81, Id);

        Assert.False(verdict.IsOk);
        Assert.IsType<UsbTransferException>(verdict.Error);
    }
}

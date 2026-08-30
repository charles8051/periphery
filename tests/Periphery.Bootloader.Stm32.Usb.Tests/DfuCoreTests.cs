namespace Periphery.Bootloader.Stm32.Usb.Tests;

/// <summary>Pure-core tests: status decode, command encode (AN3156 golden bytes), descriptor parse.</summary>
public class DfuCoreTests
{
    [Fact]
    public void DfuStatus_decodes_status_polltimeout_state()
    {
        var s = DfuStatus.Decode([0x00, 0xE8, 0x03, 0x00, 0x05, 0x00]); // poll 0x0003E8 = 1000 ms, state 5
        Assert.Equal(DfuStatusCode.Ok, s.Status);
        Assert.Equal(1000, s.PollTimeout.TotalMilliseconds);
        Assert.Equal(DfuState.DfuDnloadIdle, s.State);
    }

    [Fact]
    public void DfuStatus_decodes_error()
    {
        var s = DfuStatus.Decode([0x01, 0, 0, 0, 0x0A, 0]);
        Assert.Equal(DfuStatusCode.ErrTarget, s.Status);
        Assert.Equal(DfuState.DfuError, s.State);
    }

    [Fact]
    public void SetAddress_encodes_little_endian_address()
        => Assert.Equal(new byte[] { 0x21, 0x00, 0x00, 0x00, 0x08 }, new Stm32DfuCommand.SetAddress(0x08000000).Encode());

    [Fact]
    public void MassErase_encodes_single_command_byte()
        => Assert.Equal(new byte[] { 0x41 }, Stm32DfuCommand.MassErase.Instance.Encode());

    [Fact]
    public void ErasePage_encodes_command_and_address()
        => Assert.Equal(new byte[] { 0x41, 0x00, 0x04, 0x00, 0x08 }, new Stm32DfuCommand.ErasePage(0x08000400).Encode());

    [Fact]
    public void ReadUnprotect_encodes_single_command_byte()
        => Assert.Equal(new byte[] { 0x92 }, Stm32DfuCommand.ReadUnprotect.Instance.Encode());

    [Fact]
    public void FunctionalDescriptor_reads_transfer_size()
    {
        byte[] blob =
        [
            9, 0x02, 0, 0, 1, 1, 0, 0x80, 50,                  // configuration descriptor
            9, 0x04, 0, 0, 0, 0xFE, 0x01, 0x02, 0,             // interface descriptor (DFU class)
            9, 0x21, 0x0B, 0xFF, 0x00, 0x00, 0x08, 0x1A, 0x01, // DFU functional: wTransferSize 0x0800 = 2048
        ];
        Assert.True(DfuFunctionalDescriptor.TryParseTransferSize(blob, out int ts));
        Assert.Equal(2048, ts);
    }

    [Fact]
    public void FunctionalDescriptor_absent_returns_false()
    {
        byte[] blob = [9, 0x02, 0, 0, 1, 1, 0, 0x80, 50]; // configuration only, no DFU functional descriptor
        Assert.False(DfuFunctionalDescriptor.TryParseTransferSize(blob, out _));
    }
}

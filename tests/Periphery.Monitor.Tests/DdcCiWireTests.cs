using System.Text;

namespace Periphery.Monitor.Tests;

public class DdcCiWireTests
{
    [Fact]
    public void EncodeGetVcp_MatchesHandComputedVector()
    {
        // chk = 0x6E ^ 0x51 ^ 0x82 ^ 0x01 ^ 0x10 = 0xAC — pins the checksum
        // seeding (display write address included virtually).
        Assert.Equal(
            new byte[] { 0x51, 0x82, 0x01, 0x10, 0xAC },
            DdcCiWire.EncodeGetVcp(0x10));
    }

    [Fact]
    public void EncodeSetVcp_MatchesHandComputedVector()
    {
        // chk = 0x6E ^ 0x51 ^ 0x84 ^ 0x03 ^ 0x10 ^ 0x00 ^ 0x32 = 0x9A.
        Assert.Equal(
            new byte[] { 0x51, 0x84, 0x03, 0x10, 0x00, 0x32, 0x9A },
            DdcCiWire.EncodeSetVcp(0x10, 0x0032));
    }

    [Fact]
    public void EncodeCapabilitiesRequest_CarriesOffsetBigEndian()
    {
        var frame = DdcCiWire.EncodeCapabilitiesRequest(0x0123);

        Assert.Equal(new byte[] { 0x51, 0x83, 0xF3, 0x01, 0x23 }, frame[..5]);
        Assert.Equal(DdcCiWire.Checksum(DdcCiWire.DisplayWriteAddress, frame.AsSpan(..^1)), frame[^1]);
    }

    private static byte[] BuildGetVcpReply(
        byte result, byte code, ushort max, ushort current, bool corruptChecksum = false)
    {
        var reply = new byte[]
        {
            0x6E, 0x88, 0x02, result, code, 0x00,
            (byte)(max >> 8), (byte)(max & 0xFF),
            (byte)(current >> 8), (byte)(current & 0xFF),
            0,
        };
        reply[^1] = DdcCiWire.Checksum(DdcCiWire.HostReplyAddress, reply.AsSpan(..^1));
        if (corruptChecksum) reply[^1] ^= 0xFF;
        return reply;
    }

    [Fact]
    public void DecodeGetVcpReply_RoundTripsValues()
    {
        var reply = BuildGetVcpReply(result: 0, code: 0x10, max: 100, current: 30);

        Assert.True(DdcCiWire.TryDecodeGetVcpReply(reply, 0x10, out var value, out var error));
        Assert.Null(error);
        Assert.Equal(30, value.Current);
        Assert.Equal(100, value.Maximum);
    }

    [Fact]
    public void DecodeGetVcpReply_RejectsChecksumCorruption()
    {
        var reply = BuildGetVcpReply(0, 0x10, 100, 30, corruptChecksum: true);

        Assert.False(DdcCiWire.TryDecodeGetVcpReply(reply, 0x10, out _, out var error));
        Assert.Contains("checksum", error);
    }

    [Fact]
    public void DecodeGetVcpReply_SurfacesUnsupportedCodeResult()
    {
        var reply = BuildGetVcpReply(result: 1, code: 0x10, max: 0, current: 0);

        Assert.False(DdcCiWire.TryDecodeGetVcpReply(reply, 0x10, out _, out var error));
        Assert.Contains("unsupported", error);
    }

    [Fact]
    public void DecodeGetVcpReply_RejectsCodeEchoMismatch()
    {
        var reply = BuildGetVcpReply(0, code: 0x12, max: 100, current: 30);

        Assert.False(DdcCiWire.TryDecodeGetVcpReply(reply, 0x10, out _, out var error));
        Assert.Contains("0x12", error);
    }

    private static byte[] BuildCapabilitiesFragment(ushort offset, string data)
    {
        var bytes = Encoding.ASCII.GetBytes(data);
        var frame = new byte[2 + 3 + bytes.Length + 1];
        frame[0] = 0x6E;
        frame[1] = (byte)(0x80 | (3 + bytes.Length));
        frame[2] = 0xE3;
        frame[3] = (byte)(offset >> 8);
        frame[4] = (byte)(offset & 0xFF);
        bytes.CopyTo(frame, 5);
        frame[^1] = DdcCiWire.Checksum(DdcCiWire.HostReplyAddress, frame.AsSpan(..^1));
        return frame;
    }

    [Fact]
    public void DecodeCapabilitiesFragment_RoundTrips()
    {
        var frame = BuildCapabilitiesFragment(0x0020, "(vcp(10 12))");

        Assert.True(DdcCiWire.TryDecodeCapabilitiesFragment(frame, out var offset, out var data, out var error));
        Assert.Null(error);
        Assert.Equal(0x0020, offset);
        Assert.Equal("(vcp(10 12))", Encoding.ASCII.GetString(data));
    }

    [Fact]
    public void DecodeCapabilitiesFragment_EmptyDataMarksEnd()
    {
        var frame = BuildCapabilitiesFragment(0x0040, "");

        Assert.True(DdcCiWire.TryDecodeCapabilitiesFragment(frame, out var offset, out var data, out _));
        Assert.Equal(0x0040, offset);
        Assert.Empty(data);
    }

    [Fact]
    public void DecodeCapabilitiesFragment_RejectsTruncatedDeclaredLength()
    {
        var frame = BuildCapabilitiesFragment(0, "abcdef");

        Assert.False(DdcCiWire.TryDecodeCapabilitiesFragment(frame.AsSpan(..6), out _, out _, out var error));
        Assert.NotNull(error);
    }
}

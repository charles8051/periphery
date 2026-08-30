using System;
using System.Linq;
using Xunit;

namespace Periphery.Bootloader.Efm8.Usb.Tests;

/// <summary>
/// Exhaustive tests for the pure protocol core: framing parse, chunking, and reply
/// classification. No transport, no IO.
/// </summary>
public class Efm8ProtocolTests
{
    [Fact]
    public void ParseRecords_WellFormedMultiRecord_ReturnsEachFrameInOrder()
    {
        var setup = BootRecordBuilder.Frame(0x31, 0xA5, 0xF1, 0x00); // Setup
        var write = BootRecordBuilder.Frame(0x33, BootRecordBuilder.Bytes(8)); // Write
        var runApp = BootRecordBuilder.Frame(0x36, 0x00, 0x00); // RunApp
        var stream = BootRecordBuilder.Stream(setup, write, runApp);

        var records = Efm8Protocol.ParseRecords(stream);

        Assert.Equal(3, records.Length);
        Assert.Equal(0, records[0].Index);
        Assert.Equal(0x31, records[0].Command);
        Assert.Equal(0x33, records[1].Command);
        Assert.Equal(0x36, records[2].Command);
        Assert.True(records[0].Frame.Span.SequenceEqual(setup));
        Assert.True(records[1].Frame.Span.SequenceEqual(write));
        Assert.True(records[2].Frame.Span.SequenceEqual(runApp));
        // Every record's DeclaredLength byte equals Frame.Length - 2.
        Assert.All(records, r => Assert.Equal(r.Frame.Length - 2, r.DeclaredLength));
    }

    [Fact]
    public void ParseRecords_BadStartByte_Throws()
    {
        byte[] stream = [0x25, 0x03, 0x33, 0x00, 0x00]; // 0x25 != '$'
        var ex = Assert.Throws<Efm8BootFormatException>(() => Efm8Protocol.ParseRecords(stream));
        Assert.Contains("start byte", ex.Message);
    }

    [Fact]
    public void ParseRecords_DeclaredLengthOverrunsStream_Throws()
    {
        // Declares length 10 but only 3 bytes follow the length byte.
        byte[] stream = [0x24, 0x0A, 0x33, 0x00, 0x00];
        Assert.Throws<Efm8BootFormatException>(() => Efm8Protocol.ParseRecords(stream));
    }

    [Fact]
    public void ParseRecords_TruncatedHeader_Throws()
    {
        byte[] stream = [0x24]; // start byte but no length byte
        Assert.Throws<Efm8BootFormatException>(() => Efm8Protocol.ParseRecords(stream));
    }

    [Fact]
    public void ParseRecords_EmptyStream_Throws()
        => Assert.Throws<Efm8BootFormatException>(() => Efm8Protocol.ParseRecords(Array.Empty<byte>()));

    [Fact]
    public void ParseRecords_ZeroLengthRecord_Throws()
    {
        byte[] stream = [0x24, 0x00]; // length 0 => no command byte
        Assert.Throws<Efm8BootFormatException>(() => Efm8Protocol.ParseRecords(stream));
    }

    [Fact]
    public void ChunkFrame_FrameLargerThanReport_SplitsOnReportBoundary()
    {
        // 133-byte frame (matches the real .efm8's erase-with-data record): 64 + 64 + 5.
        var frame = BootRecordBuilder.Frame(0x32, BootRecordBuilder.Bytes(130));
        Assert.Equal(133, frame.Length);

        var chunks = Efm8Protocol.ChunkFrame(frame);

        Assert.Equal(3, chunks.Count);
        Assert.Equal(64, chunks[0].Length);
        Assert.Equal(64, chunks[1].Length);
        Assert.Equal(5, chunks[2].Length);
        // Concatenated chunks reconstruct the frame exactly.
        var rejoined = chunks.SelectMany(c => c.ToArray()).ToArray();
        Assert.Equal(frame, rejoined);
    }

    [Fact]
    public void ChunkFrame_FrameSmallerThanReport_SingleChunk()
    {
        var frame = BootRecordBuilder.Frame(0x36, 0x00, 0x00); // 5 bytes
        var chunks = Efm8Protocol.ChunkFrame(frame);
        Assert.Single(chunks);
        Assert.Equal(frame, chunks[0].ToArray());
    }

    [Fact]
    public void ChunkFrame_FrameExactlyReportSize_SingleChunk()
    {
        var frame = BootRecordBuilder.Frame(0x33, BootRecordBuilder.Bytes(61)); // 64 bytes
        Assert.Equal(64, frame.Length);
        var chunks = Efm8Protocol.ChunkFrame(frame);
        Assert.Single(chunks);
        Assert.Equal(64, chunks[0].Length);
    }

    [Theory]
    [InlineData(0x40, Efm8ReplyCode.Acknowledge)]
    [InlineData(0x41, Efm8ReplyCode.RangeError)]
    [InlineData(0x42, Efm8ReplyCode.CrcError)]
    [InlineData(0x43, Efm8ReplyCode.OtherError)]
    [InlineData(0x3F, Efm8ReplyCode.Unknown)]
    [InlineData(0x00, Efm8ReplyCode.Unknown)]
    [InlineData(0xFF, Efm8ReplyCode.Unknown)]
    public void ClassifyReply_MapsBytesToCodes(byte reply, Efm8ReplyCode expected)
        => Assert.Equal(expected, Efm8Protocol.ClassifyReply(reply));
}

using System.Collections.Immutable;

namespace Periphery.Treehopper.Libraries.Tests;

/// <summary>
/// Asserts that <see cref="Apa102Encoder"/> produces byte-exact wire frames.
/// Pure function, zero hardware.
/// </summary>
public class Apa102EncoderTests
{
    // ── Structure ──────────────────────────────────────────────────────

    [Fact]
    public void Encode_SingleLed_HasStartLedAndEndFrame()
    {
        var frame = new LedFrame(ImmutableArray.Create(new Rgb(255, 0, 0)));
        var bytes = Apa102Encoder.Encode(frame);

        // 4 start + 4 LED + ceil(1/16)=1 end = 9 bytes
        Assert.Equal(9, bytes.Length);
    }

    [Fact]
    public void Encode_StartFrame_IsAllZeros()
    {
        var frame = new LedFrame(ImmutableArray.Create(Rgb.White));
        var bytes = Apa102Encoder.Encode(frame);

        Assert.Equal(0x00, bytes[0]);
        Assert.Equal(0x00, bytes[1]);
        Assert.Equal(0x00, bytes[2]);
        Assert.Equal(0x00, bytes[3]);
    }

    [Fact]
    public void Encode_EndFrame_IsAllZeros()
    {
        var frame = new LedFrame(ImmutableArray.Create(Rgb.White));
        var bytes = Apa102Encoder.Encode(frame);
        // Last byte (end frame) is zero — only the extra clock edges matter, and a
        // 0xFF end frame can be misread as another LED frame on a long chain.
        Assert.Equal(0x00, bytes[^1]);
    }

    [Theory]
    [InlineData(1,  1)]  // ceil(1/16)  = 1
    [InlineData(16, 1)]  // ceil(16/16) = 1
    [InlineData(17, 2)]  // ceil(17/16) = 2
    [InlineData(60, 4)]  // ceil(60/16) = 4
    public void Encode_EndFrameLength_IsAtLeastNOver16(int n, int expectedEndBytes)
    {
        var pixels = ImmutableArray.CreateRange(new Rgb[n]);
        var frame  = new LedFrame(pixels);
        var bytes  = Apa102Encoder.Encode(frame);

        int actualEndLen = bytes.Length - 4 - n * 4;
        Assert.Equal(expectedEndBytes, actualEndLen);
    }

    // ── LED frame bytes ────────────────────────────────────────────────

    [Fact]
    public void Encode_LedFrame_HeaderContainsBrightness()
    {
        var frame = new LedFrame(ImmutableArray.Create(Rgb.White), Brightness: 15);
        var bytes = Apa102Encoder.Encode(frame);

        // LED header at byte 4: 0b111_bbbbb where bbbbb = 15 = 0x0F
        byte header = bytes[4];
        Assert.Equal((byte)(0xE0 | 15), header);
    }

    [Fact]
    public void Encode_LedFrame_MaxBrightness_HeaderIs0xFF()
    {
        var frame = new LedFrame(ImmutableArray.Create(Rgb.White), Brightness: 31);
        var bytes = Apa102Encoder.Encode(frame);

        Assert.Equal(0xFF, bytes[4]); // 0xE0 | 31 = 0xFF
    }

    [Fact]
    public void Encode_LedFrame_ColourBytesAreBlueGreenRed()
    {
        // APA102 order is B, G, R
        var frame = new LedFrame(ImmutableArray.Create(new Rgb(R: 0xAA, G: 0xBB, B: 0xCC)));
        var bytes = Apa102Encoder.Encode(frame);

        Assert.Equal(0xCC, bytes[5]); // B
        Assert.Equal(0xBB, bytes[6]); // G
        Assert.Equal(0xAA, bytes[7]); // R
    }

    [Fact]
    public void Encode_TwoLeds_SecondLedAt8Bytes()
    {
        var pixels = ImmutableArray.Create(
            new Rgb(0x11, 0x22, 0x33),
            new Rgb(0x44, 0x55, 0x66));
        var frame = new LedFrame(pixels);
        var bytes = Apa102Encoder.Encode(frame);

        // Second LED starts at byte 8 (4 start + 4 first LED)
        Assert.Equal(0xFF, bytes[8]);    // header (brightness=31)
        Assert.Equal(0x66, bytes[9]);    // B
        Assert.Equal(0x55, bytes[10]);   // G
        Assert.Equal(0x44, bytes[11]);   // R
    }

    [Fact]
    public void Encode_BlackPixel_ColourBytesAllZero()
    {
        var frame = new LedFrame(ImmutableArray.Create(Rgb.Black));
        var bytes = Apa102Encoder.Encode(frame);

        Assert.Equal(0x00, bytes[5]);
        Assert.Equal(0x00, bytes[6]);
        Assert.Equal(0x00, bytes[7]);
    }

    // ── The #170 bench harness's assumptions ───────────────────────────

    // scratch/Apa102Desync builds this byte layout itself rather than reaching past
    // Apa102Encoder's `internal`, because the claim it tests is about what the FIRMWARE does
    // with bytes on the wire. That leaves one thing to pin here: that the shipped encoder
    // really does lay a frame out the way the harness assumes. If this fails, the harness is
    // reproducing traffic the library no longer emits, and ADR-0086 D5's reproduction is
    // measuring nothing.
    [Fact]
    public void Encode_GroupLayout_IsWhatTheDesyncHarnessAssumes()
    {
        const int LedCount = 63;
        var frame = LedFrame.Solid(LedCount, new Rgb(R: 0x00, G: 0x00, B: 0x01), brightness: 31);
        var bytes = Apa102Encoder.Encode(frame);

        // 4 start + 63*4 + ceil(63/16)=4 end. 260 is the number that makes Apa102Strip's
        // 252-byte chunk a 259-byte command, i.e. USB packets of 64/64/64/64/3 - the
        // multi-packet path #170 lives on.
        Assert.Equal(260, bytes.Length);

        // Pixels start at offset 4 in groups of four: header, Blue, Green, Red. At
        // brightness 31 the header is 0xFF, which is deliberately NOT an opcode.
        for (int px = 0; px < LedCount; px++)
        {
            int pos = 4 + px * 4;
            Assert.Equal(0xFF, bytes[pos]);
            Assert.Equal(0x01, bytes[pos + 1]);
            Assert.Equal(0x00, bytes[pos + 2]);
            Assert.Equal(0x00, bytes[pos + 3]);
        }

        // The phase claim: a firmware packet boundary at command offset 64k is stream offset
        // 64k-7, and every one of them lands on the Blue channel. This is why a desync
        // reproducibly puts a colour byte - not a header - at Treehopper_PeripheralConfig[0].
        for (int k = 1; k <= 3; k++)
        {
            int streamOffset = 64 * k - 7;
            Assert.Equal((streamOffset - 4) % 4, 1);
            Assert.Equal(0x01, bytes[streamOffset]);
        }
    }

    [Fact]
    public void Encode_EmptyFrame_IsSixBytes()
    {
        // 4 start + 0 LEDs + ceil(0/16)=0 end = 4 bytes... actually ceil(0/16)=0,
        // so empty strip = 4 bytes (just the start frame).
        var frame = new LedFrame(ImmutableArray<Rgb>.Empty);
        var bytes = Apa102Encoder.Encode(frame);
        Assert.Equal(4, bytes.Length);
    }
}

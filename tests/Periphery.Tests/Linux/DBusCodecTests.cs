using Periphery.Linux.DBus.Core;

namespace Periphery.Tests.Linux;

/// <summary>
/// The D-Bus wire format (ADR-0091 D2), against bytes BlueZ and dbus-daemon actually sent, and
/// against hand-built input for what they never send.
/// </summary>
public class DBusCodecTests
{
    // ── Captured traffic ───────────────────────────────────────────────

    [Fact]
    public void MonitorStream_DecodesEveryMessage_EndToEnd()
    {
        byte[] stream = BlueZFixtures.Bytes(BlueZFixtures.MonitorStream);
        var messages = new List<DBusMessage>();
        int offset = 0;
        while (offset < stream.Length)
        {
            Assert.True(DBusCodec.TryDecode(stream.AsSpan(offset), out var message, out int consumed));
            messages.Add(message);
            offset += consumed;
        }

        Assert.Equal(92, messages.Count);
        Assert.Equal(6, messages.Count(m => m.Type == DBusMessageType.Error));
        Assert.Equal(14, messages.Count(m => m.Type == DBusMessageType.MethodReturn));
        Assert.Equal(72, messages.Count(m => m.Type == DBusMessageType.Signal));
    }

    [Fact]
    public void MonitorStream_EveryMessage_SurvivesEncodeAndDecode()
    {
        byte[] stream = BlueZFixtures.Bytes(BlueZFixtures.MonitorStream);
        int offset = 0;
        while (offset < stream.Length)
        {
            Assert.True(DBusCodec.TryDecode(stream.AsSpan(offset), out var captured, out int consumed));
            offset += consumed;

            byte[] encoded = DBusCodec.Encode(captured);
            Assert.True(DBusCodec.TryDecode(encoded, out var decoded, out int length));
            Assert.Equal(encoded.Length, length);
            Assert.Equal(encoded, DBusCodec.Encode(decoded));
            Assert.Equal(Describe(captured), Describe(decoded));
        }
    }

    [Fact]
    public void ManagedObjectsReply_DecodesHeaderAndBody()
    {
        var reply = BlueZFixtures.Message(BlueZFixtures.ManagedObjectsDisconnected);

        Assert.Equal(DBusMessageType.MethodReturn, reply.Type);
        Assert.Equal(":1.3769", reply.Sender);
        Assert.Equal(2u, reply.ReplySerial);
        Assert.Equal("a{oa{sa{sv}}}", reply.Signature);
        var objects = Assert.IsType<DBusArray>(Assert.Single(reply.Body));
        Assert.Contains(objects.Items, o => o is DBusDictEntry { Key: DBusString { Code: 'o', Value: "/org/bluez/hci0/dev_00_AA_01_01_00_01" } });
    }

    [Fact]
    public void BusError_DecodesNameAndReplySerial()
    {
        var error = BlueZFixtures.Message(BlueZFixtures.ErrorNameHasNoOwner);

        Assert.Equal(DBusMessageType.Error, error.Type);
        Assert.Equal("org.freedesktop.DBus.Error.NameHasNoOwner", error.ErrorName);
        Assert.Equal("org.freedesktop.DBus", error.Sender);
        Assert.NotNull(error.ReplySerial);
    }

    [Fact]
    public void EveryShorterPrefix_IsIncomplete()
    {
        byte[] message = BlueZFixtures.Bytes(BlueZFixtures.NameAcquired);
        foreach (int length in new[] { 0, 1, 15, 16, 17, message.Length / 2, message.Length - 1 })
            Assert.False(DBusCodec.TryDecode(message.AsSpan(0, length), out _, out _), $"prefix of {length} bytes");
    }

    [Fact]
    public void TrailingBytes_AreNotConsumed()
    {
        byte[] message = BlueZFixtures.Bytes(BlueZFixtures.NameAcquired);
        byte[] withTail = [.. message, .. new byte[] { (byte)'l', 1, 2 }];

        Assert.True(DBusCodec.TryDecode(withTail, out _, out int consumed));
        Assert.Equal(message.Length, consumed);
    }

    // ── Byte order ─────────────────────────────────────────────────────

    [Fact]
    public void BigEndian_HandBuiltMethodReturn_Decodes()
    {
        // A method return, serial 7, answering serial 3, with body "u" = 0x01020304. Header fields
        // REPLY_SERIAL (8 bytes) and SIGNATURE (7 bytes) are 15 bytes, padded to 16 before the body.
        byte[] bytes =
        [
            (byte)'B', 2, 0, 1, 0, 0, 0, 4, 0, 0, 0, 7, 0, 0, 0, 15,
            5, 1, (byte)'u', 0, 0, 0, 0, 3,
            8, 1, (byte)'g', 0, 1, (byte)'u', 0, 0,
            1, 2, 3, 4,
        ];

        Assert.True(DBusCodec.TryDecode(bytes, out var message, out int consumed));
        Assert.Equal(bytes.Length, consumed);
        Assert.Equal(DBusMessageType.MethodReturn, message.Type);
        Assert.Equal(7u, message.Serial);
        Assert.Equal(3u, message.ReplySerial);
        Assert.Equal("u", message.Signature);
        Assert.Equal(new DBusInteger('u', 0x01020304), Assert.Single(message.Body));
    }

    [Fact]
    public void BigEndian_CapturedReply_RoundTrips()
    {
        var captured = BlueZFixtures.Message(BlueZFixtures.ManagedObjectsConnected);

        byte[] bigEndian = DBusCodec.Encode(captured, bigEndian: true);
        Assert.Equal((byte)'B', bigEndian[0]);
        Assert.True(DBusCodec.TryDecode(bigEndian, out var decoded, out _));
        Assert.Equal(Describe(captured), Describe(decoded));
    }

    [Fact]
    public void EveryBasicType_RoundTripsInBothByteOrders()
    {
        var message = new DBusMessage
        {
            Type = DBusMessageType.Signal,
            Serial = 9,
            Path = "/p",
            Interface = "a.b",
            Member = "M",
            Signature = "ybnqiuxtdsogvay(is)a{sv}",
            Body =
            [
                new DBusInteger('y', 0xFE),
                new DBusBoolean(true),
                new DBusInteger('n', -2),
                new DBusInteger('q', 0xFFFE),
                new DBusInteger('i', int.MinValue),
                new DBusInteger('u', uint.MaxValue),
                new DBusInteger('x', long.MinValue),
                new DBusUInt64(ulong.MaxValue),
                new DBusDouble(-1.5),
                new DBusString('s', "héllo"),
                new DBusString('o', "/org/x"),
                new DBusString('g', "a{sv}"),
                new DBusVariant("as", new DBusArray("s", [new DBusString('s', "in"), new DBusString('s', "side")])),
                new DBusBytes([1, 2, 3]),
                new DBusStruct([new DBusInteger('i', 5), new DBusString('s', "five")]),
                new DBusArray("{sv}", [new DBusDictEntry(new DBusString('s', "k"), new DBusVariant("b", new DBusBoolean(false)))]),
            ],
        };

        foreach (bool bigEndian in new[] { false, true })
        {
            Assert.True(DBusCodec.TryDecode(DBusCodec.Encode(message, bigEndian), out var decoded, out _));
            Assert.Equal(Describe(message), Describe(decoded));
        }
    }

    [Fact]
    public void EmptyArray_StillPadsToItsElementAlignment()
    {
        // "at" with no elements: the length word, then padding to 8 before where an element would start.
        var message = new DBusMessage { Type = DBusMessageType.Signal, Serial = 1, Path = "/p", Interface = "a.b", Member = "M", Signature = "at", Body = [new DBusArray("t", [])] };

        byte[] bytes = DBusCodec.Encode(message);
        Assert.True(DBusCodec.TryDecode(bytes, out var decoded, out _));
        Assert.Empty(Assert.IsType<DBusArray>(Assert.Single(decoded.Body)).Items);
        Assert.Equal(0, bytes.Length % 8);
    }

    // ── Rejection ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, (byte)'x')] // unknown byte-order marker
    [InlineData(3, 2)]         // protocol version 2
    public void CorruptFixedHeader_Throws(int offset, byte value)
    {
        byte[] bytes = BlueZFixtures.Bytes(BlueZFixtures.NameAcquired);
        bytes[offset] = value;

        Assert.Throws<DBusProtocolException>(() => DBusCodec.TryDecode(bytes, out _, out _));
    }

    [Fact]
    public void ZeroSerial_Throws()
    {
        byte[] bytes = BlueZFixtures.Bytes(BlueZFixtures.NameAcquired);
        bytes.AsSpan(8, 4).Clear();

        Assert.Throws<DBusProtocolException>(() => DBusCodec.TryDecode(bytes, out _, out _));
    }

    [Fact]
    public void NonZeroPadding_Throws()
    {
        // The big-endian return above, with its one padding byte before the body set.
        byte[] bytes =
        [
            (byte)'B', 2, 0, 1, 0, 0, 0, 4, 0, 0, 0, 7, 0, 0, 0, 15,
            5, 1, (byte)'u', 0, 0, 0, 0, 3,
            8, 1, (byte)'g', 0, 1, (byte)'u', 0, 0xFF,
            1, 2, 3, 4,
        ];

        Assert.Throws<DBusProtocolException>(() => DBusCodec.TryDecode(bytes, out _, out _));
    }

    [Fact]
    public void BooleanOtherThanZeroOrOne_Throws()
    {
        byte[] bytes = Encode("b", new DBusBoolean(true));
        bytes[^4] = 2; // little-endian: the low byte of the last word

        Assert.Throws<DBusProtocolException>(() => DBusCodec.TryDecode(bytes, out _, out _));
    }

    [Fact]
    public void InvalidUtf8String_Throws()
    {
        byte[] bytes = Encode("s", new DBusString('s', "ab"));
        bytes[^3] = 0xC3; // "a" becomes the lead byte of a two-byte sequence that "b" does not continue

        Assert.Throws<DBusProtocolException>(() => DBusCodec.TryDecode(bytes, out _, out _));
    }

    [Fact]
    public void StringWithEmbeddedNul_Throws()
    {
        byte[] bytes = Encode("s", new DBusString('s', "ab"));
        bytes[^3] = 0;

        Assert.Throws<DBusProtocolException>(() => DBusCodec.TryDecode(bytes, out _, out _));
    }

    [Fact]
    public void SignalWithoutMember_Throws()
    {
        byte[] bytes = DBusCodec.Encode(new DBusMessage { Type = DBusMessageType.Signal, Serial = 1, Path = "/p", Interface = "a.b" });

        Assert.Throws<DBusProtocolException>(() => DBusCodec.TryDecode(bytes, out _, out _));
    }

    [Fact]
    public void VariantsNestedPastTheLimit_Throw()
    {
        DBusValue value = new DBusInteger('i', 1);
        string signature = "i";
        for (int i = 0; i < 70; i++)
        {
            value = new DBusVariant(signature, value);
            signature = "v";
        }

        byte[] bytes = Encode("v", value);

        Assert.Throws<DBusProtocolException>(() => DBusCodec.TryDecode(bytes, out _, out _));
    }

    [Fact]
    public void OversizedLength_Throws()
    {
        byte[] bytes = BlueZFixtures.Bytes(BlueZFixtures.NameAcquired);
        bytes[7] = 0x10; // body length becomes 256 MiB + the original

        Assert.Throws<DBusProtocolException>(() => DBusCodec.TryDecode(bytes, out _, out _));
    }

    [Fact]
    public void Encode_RejectsZeroSerial_AndBodyThatDoesNotMatchTheSignature()
    {
        Assert.Throws<ArgumentException>(() => DBusCodec.Encode(new DBusMessage { Type = DBusMessageType.MethodCall, Path = "/", Member = "M" }));
        Assert.Throws<ArgumentException>(() => DBusCodec.Encode(new DBusMessage
        {
            Type = DBusMessageType.MethodCall, Serial = 1, Path = "/", Member = "M", Signature = "s", Body = [new DBusBoolean(true)],
        }));
        Assert.Throws<ArgumentException>(() => DBusCodec.Encode(new DBusMessage
        {
            Type = DBusMessageType.MethodCall, Serial = 1, Path = "/", Member = "M", Signature = "ss", Body = [new DBusString('s', "one")],
        }));
    }

    // ── Signatures ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("a{oa{sa{sv}}}")]
    [InlineData("sa{sv}as")]
    [InlineData("(ii)a(yv)")]
    [InlineData("aai")]
    public void ValidSignatures_Pass(string signature) => DBusSignature.Validate(signature);

    [Theory]
    [InlineData("a")]
    [InlineData("{sv}")]
    [InlineData("a{vs}")]
    [InlineData("a{sss}")]
    [InlineData("()")]
    [InlineData("(i")]
    [InlineData("z")]
    public void InvalidSignatures_Throw(string signature) =>
        Assert.Throws<DBusProtocolException>(() => DBusSignature.Validate(signature));

    [Fact]
    public void SignatureLimits_AreEnforced()
    {
        Assert.Throws<DBusProtocolException>(() => DBusSignature.Validate(new string('a', 33) + "i"));
        Assert.Throws<DBusProtocolException>(() => DBusSignature.Validate(new string('(', 33) + "i" + new string(')', 33)));
        Assert.Throws<DBusProtocolException>(() => DBusSignature.Validate(new string('i', 256)));
        DBusSignature.Validate(new string('a', 32) + "i");
    }

    [Fact]
    public void VariantSignature_MustBeOneType()
    {
        Assert.Throws<DBusProtocolException>(() => DBusSignature.ValidateSingle("ii"));
        Assert.Throws<DBusProtocolException>(() => DBusSignature.ValidateSingle(""));
        DBusSignature.ValidateSingle("a{sv}");
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static byte[] Encode(string signature, DBusValue value) => DBusCodec.Encode(new DBusMessage
    {
        Type = DBusMessageType.Signal,
        Serial = 1,
        Path = "/p",
        Interface = "a.b",
        Member = "M",
        Signature = signature,
        Body = [value],
    });

    // A canonical text form, since records holding ImmutableArray compare by reference.
    internal static string Describe(DBusMessage m) =>
        $"{m.Type} {m.Flags} {m.Serial} path={m.Path} if={m.Interface} member={m.Member} error={m.ErrorName} " +
        $"reply={m.ReplySerial} dest={m.Destination} sender={m.Sender} sig={m.Signature} body=[{string.Join(", ", m.Body.Select(Describe))}]";

    internal static string Describe(DBusValue value) => value switch
    {
        DBusString s => $"{s.Code}:{s.Value}",
        DBusBoolean b => $"b:{b.Value}",
        DBusInteger i => $"{i.Code}:{i.Value}",
        DBusUInt64 t => $"t:{t.Value}",
        DBusDouble d => $"d:{d.Value:R}",
        DBusBytes bytes => $"ay:{Convert.ToHexString(bytes.Value.AsSpan())}",
        DBusArray a => $"a{a.ElementSignature}[{string.Join(", ", a.Items.Select(Describe))}]",
        DBusStruct st => $"({string.Join(", ", st.Fields.Select(Describe))})",
        DBusDictEntry e => $"{{{Describe(e.Key)}: {Describe(e.Value)}}}",
        DBusVariant v => $"v<{v.Signature}>{Describe(v.Value)}",
        _ => throw new InvalidOperationException(value.GetType().Name),
    };
}

// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Periphery.Linux.DBus.Core;

/// <summary>
/// The D-Bus message format, both ways, as pure functions over bytes (ADR-0091 D2). It reads both
/// byte orders and any type a signature can name, and rejects anything the specification does not
/// allow with a <see cref="DBusProtocolException"/>.
/// </summary>
internal static class DBusCodec
{
    /// <summary>The longest message the specification allows: 128 MiB.</summary>
    internal const int MaxMessageLength = 1 << 27;

    /// <summary>The longest array the specification allows: 64 MiB.</summary>
    internal const int MaxArrayLength = 1 << 26;

    private const int FixedHeaderLength = 16;

    // Arrays, structs and variants together. Signatures cap arrays and structs at 32 each; the
    // total also bounds variants, which a signature cannot see into.
    private const int MaxDepth = 64;

    private const byte FieldPath = 1;
    private const byte FieldInterface = 2;
    private const byte FieldMember = 3;
    private const byte FieldErrorName = 4;
    private const byte FieldReplySerial = 5;
    private const byte FieldDestination = 6;
    private const byte FieldSender = 7;
    private const byte FieldSignature = 8;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Decodes the message at the start of <paramref name="buffer"/>.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> when <paramref name="buffer"/> does not yet hold a whole message.
    /// </returns>
    /// <exception cref="DBusProtocolException">The bytes are not a valid message.</exception>
    internal static bool TryDecode(ReadOnlySpan<byte> buffer, [NotNullWhen(true)] out DBusMessage? message, out int consumed)
    {
        message = null;
        consumed = 0;
        if (buffer.Length < FixedHeaderLength)
            return false;

        bool little = buffer[0] switch
        {
            (byte)'l' => true,
            (byte)'B' => false,
            _ => throw new DBusProtocolException($"Unknown byte-order marker 0x{buffer[0]:X2}."),
        };
        if (buffer[3] != 1)
            throw new DBusProtocolException($"Unsupported protocol version {buffer[3]}.");

        uint bodyLength = ReadUInt32(buffer[4..], little);
        uint serial = ReadUInt32(buffer[8..], little);
        uint fieldsLength = ReadUInt32(buffer[12..], little);
        if (fieldsLength > MaxArrayLength)
            throw new DBusProtocolException($"Header fields are {fieldsLength} bytes.");

        long headerEnd = Align(FixedHeaderLength + (long)fieldsLength, 8);
        long total = headerEnd + bodyLength;
        if (total > MaxMessageLength)
            throw new DBusProtocolException($"Message is {total} bytes; the limit is {MaxMessageLength}.");
        if (buffer.Length < total)
            return false;
        if (serial == 0)
            throw new DBusProtocolException("Message serial is zero.");

        var reader = new Reader(buffer[..(int)total], little, FixedHeaderLength);
        var fields = new HeaderFields();
        int fieldsEnd = FixedHeaderLength + (int)fieldsLength;
        while (reader.Position < fieldsEnd)
        {
            reader.Align(8);
            byte code = reader.ReadByte();
            string signature = reader.ReadSignature();
            DBusSignature.ValidateSingle(signature);
            int index = 0;
            fields.Set(code, signature, reader.ReadValue(signature, ref index, 0));
        }
        if (reader.Position != fieldsEnd)
            throw new DBusProtocolException("Header fields overrun their declared length.");
        reader.Align(8);

        string bodySignature = fields.Signature ?? "";
        DBusSignature.Validate(bodySignature);
        var body = ImmutableArray.CreateBuilder<DBusValue>();
        int bodyIndex = 0;
        while (bodyIndex < bodySignature.Length)
            body.Add(reader.ReadValue(bodySignature, ref bodyIndex, 0));
        if (reader.Position != total)
            throw new DBusProtocolException("Body does not fill its declared length.");

        var type = (DBusMessageType)buffer[1];
        fields.CheckRequired(type);

        message = new DBusMessage
        {
            Type = type,
            Flags = (DBusMessageFlags)buffer[2],
            Serial = serial,
            Path = fields.Path,
            Interface = fields.Interface,
            Member = fields.Member,
            ErrorName = fields.ErrorName,
            ReplySerial = fields.ReplySerial,
            Destination = fields.Destination,
            Sender = fields.Sender,
            Signature = bodySignature,
            Body = body.ToImmutable(),
        };
        consumed = (int)total;
        return true;
    }

    /// <summary>
    /// Encodes <paramref name="message"/>. Little-endian unless <paramref name="bigEndian"/>, which
    /// exists so tests can build big-endian input.
    /// </summary>
    /// <exception cref="ArgumentException">The body does not match the signature, or the serial is zero.</exception>
    internal static byte[] Encode(DBusMessage message, bool bigEndian = false)
    {
        if (message.Serial == 0)
            throw new ArgumentException("A message needs a non-zero serial.", nameof(message));
        DBusSignature.Validate(message.Signature);

        var writer = new Writer(!bigEndian);
        writer.WriteByte(bigEndian ? (byte)'B' : (byte)'l');
        writer.WriteByte((byte)message.Type);
        writer.WriteByte((byte)message.Flags);
        writer.WriteByte(1);
        writer.WriteUInt32(0);
        writer.WriteUInt32(message.Serial);
        writer.WriteUInt32(0);

        writer.WriteStringField(FieldPath, 'o', message.Path);
        writer.WriteStringField(FieldInterface, 's', message.Interface);
        writer.WriteStringField(FieldMember, 's', message.Member);
        writer.WriteStringField(FieldErrorName, 's', message.ErrorName);
        if (message.ReplySerial is { } replySerial)
        {
            writer.Pad(8);
            writer.WriteByte(FieldReplySerial);
            writer.WriteSignature("u");
            writer.WriteUInt32(replySerial);
        }
        writer.WriteStringField(FieldDestination, 's', message.Destination);
        writer.WriteStringField(FieldSender, 's', message.Sender);
        if (message.Signature.Length > 0)
            writer.WriteStringField(FieldSignature, 'g', message.Signature);
        writer.PatchUInt32(12, (uint)(writer.Position - FixedHeaderLength));
        writer.Pad(8);

        int bodyStart = writer.Position;
        int index = 0;
        foreach (var value in message.Body)
        {
            if (index >= message.Signature.Length)
                throw new ArgumentException("The body has more values than the signature has types.", nameof(message));
            writer.WriteValue(message.Signature, ref index, value);
        }
        if (index != message.Signature.Length)
            throw new ArgumentException("The body has fewer values than the signature has types.", nameof(message));
        writer.PatchUInt32(4, (uint)(writer.Position - bodyStart));

        return writer.ToArray();
    }

    private static long Align(long position, int alignment) => (position + alignment - 1) & ~(long)(alignment - 1);

    private static uint ReadUInt32(ReadOnlySpan<byte> span, bool little) =>
        little ? BinaryPrimitives.ReadUInt32LittleEndian(span) : BinaryPrimitives.ReadUInt32BigEndian(span);

    private sealed class HeaderFields
    {
        public string? Path, Interface, Member, ErrorName, Destination, Sender, Signature;
        public uint? ReplySerial;

        public void Set(byte code, string signature, DBusValue value)
        {
            switch (code)
            {
                case FieldPath: Path = Expect(value, signature, 'o'); break;
                case FieldInterface: Interface = Expect(value, signature, 's'); break;
                case FieldMember: Member = Expect(value, signature, 's'); break;
                case FieldErrorName: ErrorName = Expect(value, signature, 's'); break;
                case FieldDestination: Destination = Expect(value, signature, 's'); break;
                case FieldSender: Sender = Expect(value, signature, 's'); break;
                case FieldSignature: Signature = Expect(value, signature, 'g'); break;
                case FieldReplySerial:
                    if (value is not DBusInteger { Code: 'u', Value: var serial })
                        throw new DBusProtocolException($"Header field REPLY_SERIAL has type '{signature}'.");
                    ReplySerial = (uint)serial;
                    break;
                default:
                    // Unknown fields, and UNIX_FDS, which this client never negotiates, are ignored.
                    break;
            }
        }

        public void CheckRequired(DBusMessageType type)
        {
            bool complete = type switch
            {
                DBusMessageType.MethodCall => Path is not null && Member is not null,
                DBusMessageType.MethodReturn => ReplySerial is not null,
                DBusMessageType.Error => ErrorName is not null && ReplySerial is not null,
                DBusMessageType.Signal => Path is not null && Interface is not null && Member is not null,
                _ => true,
            };
            if (!complete)
                throw new DBusProtocolException($"A {type} message lacks a required header field.");
        }

        private static string Expect(DBusValue value, string signature, char code) =>
            value is DBusString s && s.Code == code
                ? s.Value
                : throw new DBusProtocolException($"A header field has type '{signature}', expected '{code}'.");
    }

    private ref struct Reader
    {
        private readonly ReadOnlySpan<byte> _data;
        private readonly bool _little;
        private int _position;

        public Reader(ReadOnlySpan<byte> data, bool little, int position)
        {
            _data = data;
            _little = little;
            _position = position;
        }

        public readonly int Position => _position;

        public void Align(int alignment)
        {
            int target = (int)DBusCodec.Align(_position, alignment);
            if (target > _data.Length)
                throw new DBusProtocolException("Padding runs past the end of the message.");
            for (int i = _position; i < target; i++)
            {
                if (_data[i] != 0)
                    throw new DBusProtocolException("Alignment padding is not zero.");
            }
            _position = target;
        }

        public byte ReadByte() => Take(1)[0];

        public DBusValue ReadValue(string signature, ref int index, int depth)
        {
            if (depth > MaxDepth)
                throw new DBusProtocolException($"Values nest deeper than {MaxDepth}.");

            char code = signature[index];
            switch (code)
            {
                case 'y':
                    index++;
                    return new DBusInteger('y', ReadByte());
                case 'b':
                {
                    index++;
                    Align(4);
                    uint raw = ReadUInt32();
                    if (raw > 1)
                        throw new DBusProtocolException($"Boolean holds {raw}.");
                    return new DBusBoolean(raw == 1);
                }
                case 'n':
                    index++;
                    Align(2);
                    return new DBusInteger('n', (short)ReadUInt16());
                case 'q':
                    index++;
                    Align(2);
                    return new DBusInteger('q', ReadUInt16());
                case 'i':
                    index++;
                    Align(4);
                    return new DBusInteger('i', (int)ReadUInt32());
                case 'u':
                case 'h':
                    index++;
                    Align(4);
                    return new DBusInteger(code, ReadUInt32());
                case 'x':
                    index++;
                    Align(8);
                    return new DBusInteger('x', (long)ReadUInt64());
                case 't':
                    index++;
                    Align(8);
                    return new DBusUInt64(ReadUInt64());
                case 'd':
                    index++;
                    Align(8);
                    return new DBusDouble(BitConverter.Int64BitsToDouble((long)ReadUInt64()));
                case 's':
                case 'o':
                    index++;
                    return new DBusString(code, ReadString());
                case 'g':
                {
                    index++;
                    string value = ReadSignature();
                    DBusSignature.Validate(value);
                    return new DBusString('g', value);
                }
                case 'v':
                {
                    index++;
                    string inner = ReadSignature();
                    DBusSignature.ValidateSingle(inner);
                    int innerIndex = 0;
                    return new DBusVariant(inner, ReadValue(inner, ref innerIndex, depth + 1));
                }
                case 'a':
                    return ReadArray(signature, ref index, depth);
                case '(':
                {
                    Align(8);
                    index++;
                    var fields = ImmutableArray.CreateBuilder<DBusValue>();
                    while (signature[index] != ')')
                        fields.Add(ReadValue(signature, ref index, depth + 1));
                    index++;
                    return new DBusStruct(fields.ToImmutable());
                }
                case '{':
                {
                    Align(8);
                    index++;
                    var key = ReadValue(signature, ref index, depth + 1);
                    var value = ReadValue(signature, ref index, depth + 1);
                    index++; // '}'
                    return new DBusDictEntry(key, value);
                }
                default:
                    throw new DBusProtocolException($"Invalid type code '{code}'.");
            }
        }

        private DBusValue ReadArray(string signature, ref int index, int depth)
        {
            Align(4);
            uint length = ReadUInt32();
            if (length > MaxArrayLength)
                throw new DBusProtocolException($"Array is {length} bytes; the limit is {MaxArrayLength}.");

            int elementStart = index + 1;
            int elementEnd = DBusSignature.SkipElementType(signature, elementStart);
            string elementSignature = signature[elementStart..elementEnd];

            // The padding before the first element is present even when the array is empty.
            Align(DBusSignature.Alignment(signature[elementStart]));
            long end = (long)_position + length;
            if (end > _data.Length)
                throw new DBusProtocolException("Array runs past the end of the message.");

            index = elementEnd;
            if (elementSignature == "y")
            {
                var bytes = _data.Slice(_position, (int)length).ToArray().ToImmutableArray();
                _position = (int)end;
                return new DBusBytes(bytes);
            }

            var items = ImmutableArray.CreateBuilder<DBusValue>();
            while (_position < end)
            {
                int elementIndex = elementStart;
                items.Add(ReadValue(signature, ref elementIndex, depth + 1));
            }
            if (_position != end)
                throw new DBusProtocolException("Array elements overrun the array's declared length.");
            return new DBusArray(elementSignature, items.ToImmutable());
        }

        private string ReadString()
        {
            Align(4);
            uint length = ReadUInt32();
            if (length > int.MaxValue - 1 || _position + (long)length + 1 > _data.Length)
                throw new DBusProtocolException("String runs past the end of the message.");
            var text = Take((int)length);
            if (ReadByte() != 0)
                throw new DBusProtocolException("String is not nul-terminated.");
            if (text.IndexOf((byte)0) >= 0)
                throw new DBusProtocolException("String holds a nul byte.");
            try
            {
                return StrictUtf8.GetString(text);
            }
            catch (DecoderFallbackException)
            {
                throw new DBusProtocolException("String is not valid UTF-8.");
            }
        }

        public string ReadSignature()
        {
            int length = ReadByte();
            var text = Take(length);
            if (ReadByte() != 0)
                throw new DBusProtocolException("Signature is not nul-terminated.");
            return Encoding.ASCII.GetString(text);
        }

        private ushort ReadUInt16()
        {
            var span = Take(2);
            return _little ? BinaryPrimitives.ReadUInt16LittleEndian(span) : BinaryPrimitives.ReadUInt16BigEndian(span);
        }

        private uint ReadUInt32()
        {
            var span = Take(4);
            return _little ? BinaryPrimitives.ReadUInt32LittleEndian(span) : BinaryPrimitives.ReadUInt32BigEndian(span);
        }

        private ulong ReadUInt64()
        {
            var span = Take(8);
            return _little ? BinaryPrimitives.ReadUInt64LittleEndian(span) : BinaryPrimitives.ReadUInt64BigEndian(span);
        }

        private ReadOnlySpan<byte> Take(int count)
        {
            if (_position + (long)count > _data.Length)
                throw new DBusProtocolException("Value runs past the end of the message.");
            var span = _data.Slice(_position, count);
            _position += count;
            return span;
        }
    }

    private sealed class Writer
    {
        private readonly bool _little;
        private byte[] _buffer = new byte[256];

        public Writer(bool little) => _little = little;

        public int Position { get; private set; }

        public byte[] ToArray() => _buffer.AsSpan(0, Position).ToArray();

        public void Pad(int alignment)
        {
            int target = (int)Align(Position, alignment);
            Reserve(target - Position).Clear();
        }

        public void WriteByte(byte value) => Reserve(1)[0] = value;

        public void WriteUInt32(uint value)
        {
            var span = Reserve(4);
            if (_little) BinaryPrimitives.WriteUInt32LittleEndian(span, value);
            else BinaryPrimitives.WriteUInt32BigEndian(span, value);
        }

        public void PatchUInt32(int offset, uint value)
        {
            var span = _buffer.AsSpan(offset, 4);
            if (_little) BinaryPrimitives.WriteUInt32LittleEndian(span, value);
            else BinaryPrimitives.WriteUInt32BigEndian(span, value);
        }

        public void WriteSignature(string signature)
        {
            WriteByte((byte)signature.Length);
            Encoding.ASCII.GetBytes(signature, Reserve(signature.Length));
            WriteByte(0);
        }

        public void WriteStringField(byte code, char type, string? value)
        {
            if (value is null)
                return;
            Pad(8);
            WriteByte(code);
            WriteSignature(type.ToString());
            if (type == 'g') WriteSignature(value);
            else WriteString(value);
        }

        public void WriteValue(string signature, ref int index, DBusValue value)
        {
            char code = signature[index];
            switch (code)
            {
                case 'y':
                    index++;
                    WriteByte((byte)Integer(value, code));
                    break;
                case 'b':
                    index++;
                    Pad(4);
                    WriteUInt32(value is DBusBoolean b ? (b.Value ? 1u : 0u) : throw Mismatch(value, code));
                    break;
                case 'n':
                case 'q':
                {
                    index++;
                    Pad(2);
                    var span = Reserve(2);
                    ushort raw = (ushort)Integer(value, code);
                    if (_little) BinaryPrimitives.WriteUInt16LittleEndian(span, raw);
                    else BinaryPrimitives.WriteUInt16BigEndian(span, raw);
                    break;
                }
                case 'i':
                case 'u':
                case 'h':
                    index++;
                    Pad(4);
                    WriteUInt32((uint)Integer(value, code));
                    break;
                case 'x':
                    index++;
                    Pad(8);
                    WriteUInt64((ulong)Integer(value, code));
                    break;
                case 't':
                    index++;
                    Pad(8);
                    WriteUInt64(value is DBusUInt64 t ? t.Value : throw Mismatch(value, code));
                    break;
                case 'd':
                    index++;
                    Pad(8);
                    WriteUInt64(value is DBusDouble d ? (ulong)BitConverter.DoubleToInt64Bits(d.Value) : throw Mismatch(value, code));
                    break;
                case 's':
                case 'o':
                    index++;
                    WriteString(Text(value, code));
                    break;
                case 'g':
                    index++;
                    WriteSignature(Text(value, code));
                    break;
                case 'v':
                {
                    index++;
                    if (value is not DBusVariant variant)
                        throw Mismatch(value, code);
                    WriteSignature(variant.Signature);
                    int innerIndex = 0;
                    WriteValue(variant.Signature, ref innerIndex, variant.Value);
                    break;
                }
                case 'a':
                    WriteArray(signature, ref index, value);
                    break;
                case '(':
                {
                    if (value is not DBusStruct s)
                        throw Mismatch(value, code);
                    Pad(8);
                    index++;
                    foreach (var field in s.Fields)
                        WriteValue(signature, ref index, field);
                    if (signature[index] != ')')
                        throw new ArgumentException("A struct has fewer fields than its signature.");
                    index++;
                    break;
                }
                case '{':
                {
                    if (value is not DBusDictEntry entry)
                        throw Mismatch(value, code);
                    Pad(8);
                    index++;
                    WriteValue(signature, ref index, entry.Key);
                    WriteValue(signature, ref index, entry.Value);
                    index++;
                    break;
                }
                default:
                    throw new ArgumentException($"Invalid type code '{code}'.");
            }
        }

        private void WriteArray(string signature, ref int index, DBusValue value)
        {
            int elementStart = index + 1;
            int elementEnd = DBusSignature.SkipElementType(signature, elementStart);
            Pad(4);
            int lengthOffset = Position;
            WriteUInt32(0);
            Pad(DBusSignature.Alignment(signature[elementStart]));
            int start = Position;

            switch (value)
            {
                case DBusBytes bytes when signature[elementStart] == 'y':
                    bytes.Value.AsSpan().CopyTo(Reserve(bytes.Value.Length));
                    break;
                case DBusArray array:
                    foreach (var item in array.Items)
                    {
                        int elementIndex = elementStart;
                        WriteValue(signature, ref elementIndex, item);
                    }
                    break;
                default:
                    throw Mismatch(value, 'a');
            }

            PatchUInt32(lengthOffset, (uint)(Position - start));
            index = elementEnd;
        }

        private void WriteString(string value)
        {
            Pad(4);
            int length = Encoding.UTF8.GetByteCount(value);
            WriteUInt32((uint)length);
            Encoding.UTF8.GetBytes(value, Reserve(length));
            WriteByte(0);
        }

        private void WriteUInt64(ulong value)
        {
            var span = Reserve(8);
            if (_little) BinaryPrimitives.WriteUInt64LittleEndian(span, value);
            else BinaryPrimitives.WriteUInt64BigEndian(span, value);
        }

        private Span<byte> Reserve(int count)
        {
            if (Position + count > _buffer.Length)
                Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, Position + count));
            var span = _buffer.AsSpan(Position, count);
            Position += count;
            return span;
        }

        private static long Integer(DBusValue value, char code) =>
            value is DBusInteger integer && integer.Code == code ? integer.Value : throw Mismatch(value, code);

        private static string Text(DBusValue value, char code) =>
            value is DBusString s && s.Code == code ? s.Value : throw Mismatch(value, code);

        private static ArgumentException Mismatch(DBusValue value, char code) =>
            new($"A {value.GetType().Name} cannot be written as '{code}'.");
    }
}

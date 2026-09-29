using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenVersus.Server.Core.Hydra;

/// <summary>The Hydra binary format's type codes (big-endian throughout).</summary>
public static class HydraCode
{
    public const byte Zero = 0x00, Null = 0x01, True = 0x02, False = 0x03, WebSocket = 0x06;
    public const byte Int8 = 0x10, UInt8 = 0x11, Int16 = 0x12, UInt16 = 0x13, Int32 = 0x14, UInt32 = 0x15, Int64 = 0x16, UInt64 = 0x17;
    public const byte Float = 0x20, Double = 0x21;
    public const byte Char8 = 0x30, Char16 = 0x31, Char32 = 0x32, Bytes8 = 0x33, Bytes16 = 0x34, Bytes32 = 0x35, BigInt = 0x36;
    public const byte Date = 0x40;
    public const byte Array8 = 0x50, Array16 = 0x51, Array32 = 0x52, Array64 = 0x53;
    public const byte Map8 = 0x60, Map16 = 0x61, Map32 = 0x62, Map64 = 0x63;
    public const byte Compressed = 0x67, Localization = 0x68, Calendar = 0x69, FileReference = 0x70, StoreEnabled = 0x71;
}

/// <summary>
/// Writes values in the Hydra binary format (<c>application/x-ag-binary</c>), byte for byte as the TS server's encoder
/// (mvs-dump's HydraEncoder) does: that is what the game has accepted from OpenVersus all along, and what
/// tests/OpenVersus.Server.Core.Tests/Hydra/encoder-fixtures.json pins, generated from that encoder.
/// <para>
/// Values are JSON nodes with the TS data's conventions. A number is written as the smallest unsigned type that holds
/// it, any negative integer as INT64, anything else as DOUBLE. Objects whose key is one of the wrappers become Hydra's
/// special types: <c>_hydra_unix_date</c> (DATE), <c>_hydra_double</c> (a DOUBLE even when whole), <c>_hydra_compressed</c>,
/// <c>_hydra_calendar</c>, <c>localizations</c>, <c>_hydra_StoreEnabed</c> (sic) and <c>file_reference</c>. As in mvs-dump,
/// a wrapper replaces the most recently written piece, which is its object's map header when the wrapper is the
/// object's first key (the only way the TS data uses them); the pieces are kept as mvs-dump keeps them so that even
/// other uses come out the same.
/// </para>
/// </summary>
public sealed class HydraEncoder
{
    private readonly List<byte[]> _chunks = [];
    private readonly bool _webSocket;
    private int _length;

    private HydraEncoder(bool webSocket)
    {
        _webSocket = webSocket;
    }

    /// <summary>
    /// Encodes <paramref name="value"/>. For the websocket, the message is framed as the game expects: 0x06 and the
    /// payload's length as 16 bits, so a websocket message over 65,535 bytes cannot be sent (the TS server cannot either).
    /// </summary>
    public static byte[] Encode(JsonNode? value, bool webSocket = false)
    {
        var encoder = new HydraEncoder(webSocket);
        encoder.Value(value);
        return encoder.Result();
    }

    private byte[] Result()
    {
        if (_webSocket)
        {
            if (_length > ushort.MaxValue)
            {
                throw new HydraFormatException($"a websocket message holds at most {ushort.MaxValue} bytes; this one is {_length}");
            }

            var header = new byte[3];
            header[0] = HydraCode.WebSocket;
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(1), (ushort)_length);
            _chunks.Insert(0, header);
            _length += 3;
        }

        var result = new byte[_length];
        int at = 0;
        foreach (var chunk in _chunks)
        {
            chunk.CopyTo(result, at);
            at += chunk.Length;
        }

        return result;
    }

    private void Push(byte[] chunk)
    {
        _chunks.Add(chunk);
        _length += chunk.Length;
    }

    private void PopLast()
    {
        _length -= _chunks[^1].Length;
        _chunks.RemoveAt(_chunks.Count - 1);
    }

    private void Value(JsonNode? node)
    {
        switch (node)
        {
            case null:
                Push([HydraCode.Null]);
                break;
            case JsonObject obj:
                Object(obj);
                break;
            case JsonArray array:
                Array(array);
                break;
            case JsonValue value:
                Scalar(value);
                break;
        }
    }

    // A JsonValue holds either a parsed JsonElement or the CLR value it was made from, and does not convert between
    // numeric types on request, so the stored value is looked at directly.
    private void Scalar(JsonValue value)
    {
        if (!value.TryGetValue(out object? raw))
        {
            throw new HydraFormatException("cannot read a JSON value");
        }

        switch (raw)
        {
            case JsonElement element:
                Element(element);
                break;
            case bool b:
                Push([b ? HydraCode.True : HydraCode.False]);
                break;
            case string s:
                String(s);
                break;
            case char c:
                String(c.ToString());
                break;
            case byte[] bytes:
                Bytes(bytes);
                break;
            case ulong u:
                Integer(false, 0, u);
                break;
            case sbyte or byte or short or ushort or int or uint or long:
                long l = Convert.ToInt64(raw);
                Integer(l < 0, l, (ulong)l);
                break;
            case float or double or decimal:
                Number(Convert.ToDouble(raw));
                break;
            default:
                throw new HydraFormatException($"cannot encode a {raw?.GetType().Name ?? "null"}");
        }
    }

    private void Element(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                Push([HydraCode.Null]);
                break;
            case JsonValueKind.True:
                Push([HydraCode.True]);
                break;
            case JsonValueKind.False:
                Push([HydraCode.False]);
                break;
            case JsonValueKind.String:
                String(element.GetString()!);
                break;
            case JsonValueKind.Number when element.TryGetInt64(out long l):
                Integer(l < 0, l, (ulong)l);
                break;
            case JsonValueKind.Number when element.TryGetUInt64(out ulong u):
                Integer(false, 0, u);
                break;
            case JsonValueKind.Number:
                Number(element.GetDouble());
                break;
            default:
                Value(JsonNode.Parse(element.GetRawText()));
                break;
        }
    }

    // mvs-dump's encodeNumber: JavaScript numbers, so the only question is whether the value is whole.
    private void Number(double d)
    {
        if (double.IsFinite(d) && d == Math.Floor(d))
        {
            if (d < 0)
            {
                Integer(true, (long)d, 0);
            }
            else if (d <= ulong.MaxValue)
            {
                Integer(false, 0, (ulong)d);
            }
            else
            {
                throw new HydraFormatException($"{d} is too large for any Hydra integer");
            }

            return;
        }

        Double(d);
    }

    private void Integer(bool negative, long signed, ulong unsigned)
    {
        if (negative)
        {
            var chunk = new byte[9];
            chunk[0] = HydraCode.Int64;
            BinaryPrimitives.WriteInt64BigEndian(chunk.AsSpan(1), signed);
            Push(chunk);
        }
        else if (unsigned <= byte.MaxValue)
        {
            Push([HydraCode.UInt8, (byte)unsigned]);
        }
        else if (unsigned <= ushort.MaxValue)
        {
            var chunk = new byte[3];
            chunk[0] = HydraCode.UInt16;
            BinaryPrimitives.WriteUInt16BigEndian(chunk.AsSpan(1), (ushort)unsigned);
            Push(chunk);
        }
        else if (unsigned <= uint.MaxValue)
        {
            var chunk = new byte[5];
            chunk[0] = HydraCode.UInt32;
            BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(1), (uint)unsigned);
            Push(chunk);
        }
        else
        {
            var chunk = new byte[9];
            chunk[0] = HydraCode.UInt64;
            BinaryPrimitives.WriteUInt64BigEndian(chunk.AsSpan(1), unsigned);
            Push(chunk);
        }
    }

    private void Double(double d)
    {
        var chunk = new byte[9];
        chunk[0] = HydraCode.Double;
        // JavaScript writes its one NaN as 7FF8000000000000; .NET's double.NaN has the sign bit set.
        BinaryPrimitives.WriteInt64BigEndian(chunk.AsSpan(1), double.IsNaN(d) && BitConverter.DoubleToInt64Bits(d) == BitConverter.DoubleToInt64Bits(double.NaN)
            ? 0x7FF8000000000000
            : BitConverter.DoubleToInt64Bits(d));
        Push(chunk);
    }

    private void String(string s)
    {
        int size = Encoding.UTF8.GetByteCount(s);
        var (code, width) = size <= byte.MaxValue ? (HydraCode.Char8, 1) : size <= ushort.MaxValue ? (HydraCode.Char16, 2) : (HydraCode.Char32, 4);
        var chunk = new byte[1 + width + size];
        chunk[0] = code;
        WriteLength(chunk.AsSpan(1, width), (ulong)size);
        Encoding.UTF8.GetBytes(s, chunk.AsSpan(1 + width));
        Push(chunk);
    }

    // Not produced by the TS server (mvs-dump cannot encode bytes); here for completeness.
    private void Bytes(byte[] bytes)
    {
        var (code, width) = bytes.Length <= byte.MaxValue ? (HydraCode.Bytes8, 1) : bytes.Length <= ushort.MaxValue ? (HydraCode.Bytes16, 2) : (HydraCode.Bytes32, 4);
        var chunk = new byte[1 + width + bytes.Length];
        chunk[0] = code;
        WriteLength(chunk.AsSpan(1, width), (ulong)bytes.Length);
        bytes.CopyTo(chunk, 1 + width);
        Push(chunk);
    }

    private void Header(byte code8, int count)
    {
        var (code, width) = count <= byte.MaxValue ? (code8, 1) : count <= ushort.MaxValue ? ((byte)(code8 + 1), 2) : ((byte)(code8 + 2), 4);
        var chunk = new byte[1 + width];
        chunk[0] = code;
        WriteLength(chunk.AsSpan(1), (ulong)count);
        Push(chunk);
    }

    private void Array(JsonArray array)
    {
        Header(HydraCode.Array8, array.Count);
        foreach (var item in array)
        {
            Value(item);
        }
    }

    private void Object(JsonObject obj)
    {
        Header(HydraCode.Map8, obj.Count);
        foreach (var (key, value) in obj)
        {
            switch (key)
            {
                case "file_reference":
                    String(key);
                    FileReference(value?["value"] as JsonObject);
                    break;
                case "localizations":
                    PopLast();
                    Localizations(value as JsonObject);
                    break;
                case "_hydra_StoreEnabed":
                    PopLast();
                    Push([HydraCode.StoreEnabled, HydraCode.False, HydraCode.False]);
                    Array(value as JsonArray ?? throw new HydraFormatException("_hydra_StoreEnabed must be an array"));
                    break;
                case "_hydra_unix_date":
                    PopLast();
                    var date = new byte[5];
                    date[0] = HydraCode.Date;
                    BinaryPrimitives.WriteUInt32BigEndian(date.AsSpan(1), (uint)NumberOf(value));
                    Push(date);
                    break;
                case "_hydra_compressed":
                    PopLast();
                    Compressed(value);
                    break;
                case "_hydra_double":
                    PopLast();
                    Double(NumberOf(value));
                    break;
                case "_hydra_calendar":
                    PopLast();
                    Push([HydraCode.Calendar]);
                    Value(value?["default"]);
                    Value(value?["rendered"]);
                    break;
                default:
                    String(key);
                    Value(value);
                    break;
            }
        }
    }

    // mvs-dump: 0x68, two nulls, then the first (only) language and its text.
    private void Localizations(JsonObject? localizations)
    {
        var (language, text) = localizations?.FirstOrDefault() ?? throw new HydraFormatException("localizations must have a language");
        Push([HydraCode.Localization, HydraCode.Null, HydraCode.Null]);
        String(language);
        String(text?.GetValue<string>() ?? "");
    }

    // mvs-dump: 0x70, "hydra_file", the file's id, then the whole reference object.
    private void FileReference(JsonObject? reference)
    {
        if (reference is null)
        {
            throw new HydraFormatException("file_reference must hold { value: { id, ... } }");
        }

        Push([HydraCode.FileReference]);
        String("hydra_file");
        String(reference["id"]?.GetValue<string>() ?? "");
        Object(reference);
    }

    // mvs-dump: 0x67, index 1, then the zlib-deflated (fastest) encoding of the value as a byte string.
    private void Compressed(JsonNode? value)
    {
        byte[] inner = Encode(value);
        using var buffer = new MemoryStream();
        using (var zlib = new ZLibStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(inner);
        }

        byte[] data = buffer.ToArray();
        var (code, width) = data.Length <= byte.MaxValue ? (HydraCode.Bytes8, 1) : data.Length <= ushort.MaxValue ? (HydraCode.Bytes16, 2) : (HydraCode.Bytes32, 4);
        var header = new byte[3 + width];
        header[0] = HydraCode.Compressed;
        header[1] = 1;
        header[2] = code;
        WriteLength(header.AsSpan(3), (ulong)data.Length);
        Push(header);
        Push(data);
    }

    private static double NumberOf(JsonNode? node)
    {
        if (node is not JsonValue value || !value.TryGetValue(out object? raw))
        {
            throw new HydraFormatException("a date or double wrapper must hold a number");
        }

        return raw is JsonElement e ? e.GetDouble() : Convert.ToDouble(raw);
    }

    private static void WriteLength(Span<byte> into, ulong length)
    {
        switch (into.Length)
        {
            case 1:
                into[0] = (byte)length;
                break;
            case 2:
                BinaryPrimitives.WriteUInt16BigEndian(into, (ushort)length);
                break;
            case 4:
                BinaryPrimitives.WriteUInt32BigEndian(into, (uint)length);
                break;
            default:
                BinaryPrimitives.WriteUInt64BigEndian(into, length);
                break;
        }
    }
}

/// <summary>Bytes that are not valid Hydra, or a value that cannot be written as Hydra.</summary>
public sealed class HydraFormatException(string message) : Exception(message);

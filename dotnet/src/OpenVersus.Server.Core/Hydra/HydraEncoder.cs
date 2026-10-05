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
/// it, any negative integer as INT64, anything else as DOUBLE. An object whose only key is one of the wrappers is Hydra's
/// special type rather than a map: <c>_hydra_unix_date</c> (DATE), <c>_hydra_double</c> (a DOUBLE even when whole),
/// <c>_hydra_compressed</c>, <c>_hydra_calendar</c>, <c>localizations</c> and <c>_hydra_StoreEnabed</c> (sic); and the value
/// under a <c>file_reference</c> key is a file reference. That is how the TS data and the decoder shape them.
/// </para>
/// <para>
/// A wrapper next to other keys is refused. mvs-dump does not refuse it: it replaces whatever it wrote last (the previous
/// key's value) and leaves the map's entry count wrong, which the game would misread. No captured traffic has one.
/// </para>
/// </summary>
public sealed class HydraEncoder
{
    private readonly MemoryStream _out = new();
    private readonly bool _webSocket;
    private readonly CompressionLevel _compression;
    private readonly Func<byte[], byte[]>? _compressor;

    private HydraEncoder(bool webSocket, CompressionLevel compression, Func<byte[], byte[]>? compressor)
    {
        _webSocket = webSocket;
        _compression = compression;
        _compressor = compressor;
    }

    /// <summary>
    /// Encodes <paramref name="value"/>. For the websocket, the message is framed as the game expects: 0x06 and the
    /// payload's length in 16 bits. The frame cannot state a longer length, so a message over 65,535 bytes is refused.
    /// <paramref name="compression"/> is the zlib level for <c>_hydra_compressed</c> values: mvs-dump's (fastest) unless
    /// an answer is encoded once and kept (HissService). <paramref name="compressor"/>, when given, compresses them
    /// instead of zlib (HissZstd, for clients that read zstd).
    /// </summary>
    public static byte[] Encode(JsonNode? value, bool webSocket = false, CompressionLevel compression = CompressionLevel.Fastest, Func<byte[], byte[]>? compressor = null)
    {
        var encoder = new HydraEncoder(webSocket, compression, compressor);
        encoder.Value(value);
        return encoder.Result();
    }

    private byte[] Result()
    {
        byte[] payload = _out.ToArray();
        if (!_webSocket)
        {
            return payload;
        }

        if (payload.Length > ushort.MaxValue)
        {
            throw new HydraFormatException($"a websocket message holds at most {ushort.MaxValue} bytes; this one is {payload.Length}");
        }

        var framed = new byte[3 + payload.Length];
        framed[0] = HydraCode.WebSocket;
        BinaryPrimitives.WriteUInt16BigEndian(framed.AsSpan(1), (ushort)payload.Length);
        payload.CopyTo(framed, 3);
        return framed;
    }

    private void Push(byte[] bytes) => _out.Write(bytes);

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
            case HydraRaw encoded:
                _out.Write(encoded.Bytes);
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

    private static readonly HashSet<string> s_wrappers =
        ["_hydra_unix_date", "_hydra_double", "_hydra_compressed", "_hydra_calendar", "localizations", "_hydra_StoreEnabed"];

    private void Object(JsonObject obj)
    {
        if (obj.Count == 1 && s_wrappers.Contains(obj.First().Key))
        {
            Wrapper(obj.First().Key, obj.First().Value);
            return;
        }

        if (obj.FirstOrDefault(kv => s_wrappers.Contains(kv.Key)) is { Key: { } wrapper })
        {
            throw new HydraFormatException($"'{wrapper}' must be its object's only key");
        }

        Header(HydraCode.Map8, obj.Count);
        foreach (var (key, value) in obj)
        {
            String(key);
            if (key == "file_reference")
            {
                FileReference(value?["value"] as JsonObject);
            }
            else
            {
                Value(value);
            }
        }
    }

    private void Wrapper(string wrapper, JsonNode? value)
    {
        switch (wrapper)
        {
            case "_hydra_unix_date":
                var date = new byte[5];
                date[0] = HydraCode.Date;
                BinaryPrimitives.WriteUInt32BigEndian(date.AsSpan(1), (uint)NumberOf(value));
                Push(date);
                break;
            case "_hydra_double":
                Double(NumberOf(value));
                break;
            case "_hydra_compressed":
                Compressed(value);
                break;
            case "_hydra_calendar":
                Push([HydraCode.Calendar]);
                Value(value?["default"]);
                Value(value?["rendered"]);
                break;
            case "localizations":
                Localizations(value as JsonObject);
                break;
            case "_hydra_StoreEnabed":
                Push([HydraCode.StoreEnabled, HydraCode.False, HydraCode.False]);
                Array(value as JsonArray ?? throw new HydraFormatException("_hydra_StoreEnabed must be an array"));
                break;
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

    // mvs-dump: 0x67, index 1, then the zlib-deflated encoding of the value as a byte string. The game inflates any zlib
    // stream; the bytes differ from mvs-dump's (Node's zlib) at every level but the smallest values.
    private void Compressed(JsonNode? value)
    {
        byte[] inner = Encode(value, compression: _compression, compressor: _compressor);
        byte[] data = _compressor?.Invoke(inner) ?? Zlib(inner, _compression);
        var (code, width) = data.Length <= byte.MaxValue ? (HydraCode.Bytes8, 1) : data.Length <= ushort.MaxValue ? (HydraCode.Bytes16, 2) : (HydraCode.Bytes32, 4);
        var header = new byte[3 + width];
        header[0] = HydraCode.Compressed;
        header[1] = 1;
        header[2] = code;
        WriteLength(header.AsSpan(3), (ulong)data.Length);
        Push(header);
        Push(data);
    }

    private static byte[] Zlib(byte[] data, CompressionLevel level)
    {
        using var buffer = new MemoryStream();
        using (var zlib = new ZLibStream(buffer, level, leaveOpen: true))
        {
            zlib.Write(data);
        }

        return buffer.ToArray();
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

/// <summary>
/// A value that is already Hydra, which <see cref="HydraEncoder"/> writes as it is. /batch passes on answers it did not
/// make itself (the TS server's, its own endpoints') this way: decoding and encoding them again would keep their meaning
/// but not their bytes, as compressed data comes out of zlib differently.
/// </summary>
public sealed class HydraRaw
{
    private HydraRaw(byte[] bytes)
    {
        Bytes = bytes;
    }

    public byte[] Bytes { get; }

    /// <summary>A node holding <paramref name="bytes"/>, one encoded value. For encoding only: it has no JSON form.</summary>
    public static JsonNode Node(byte[] bytes) => JsonValue.Create(new HydraRaw(bytes))!;
}

/// <summary>Bytes that are not valid Hydra, or a value that cannot be written as Hydra.</summary>
public sealed class HydraFormatException(string message) : Exception(message);

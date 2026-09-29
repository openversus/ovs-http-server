using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;

namespace OpenVersus.Server.Core.Hydra;

/// <summary>
/// Reads the Hydra binary format into JSON nodes, with the same shapes as the TS server's decoder (mvs-dump's
/// HydraDecoder), so that what the port reads matches what the TS handlers read and <see cref="HydraEncoder"/> writes
/// it back unchanged: a whole-number DOUBLE becomes <c>{ "_hydra_double": n }</c>, DATE <c>{ "_hydra_unix_date": n }</c>,
/// and so on for the other special types.
/// <para>
/// Deliberately better than mvs-dump where that cannot change what the game is sent: 64-bit integers stay exact (it
/// went through a JavaScript number), byte strings decode to their bytes (it returned the whole buffer, and matched
/// only BYTES32), a null inside an array stays null (it left a hole, which it would then write back as NaN), and an
/// unknown type code is an error (it logged, returned null and read on out of step).
/// </para>
/// </summary>
public ref struct HydraDecoder
{
    private readonly ReadOnlySpan<byte> _buffer;
    private int _position;

    private HydraDecoder(ReadOnlySpan<byte> buffer)
    {
        _buffer = buffer;
        _position = 0;
    }

    /// <summary>Decodes one value; a websocket message's 0x06 frame is read through.</summary>
    public static JsonNode? Decode(ReadOnlySpan<byte> buffer)
    {
        var decoder = new HydraDecoder(buffer);
        var value = decoder.Value();
        if (decoder._position != buffer.Length)
        {
            throw new HydraFormatException($"{buffer.Length - decoder._position} byte(s) after the value");
        }

        return value;
    }

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || _position + count > _buffer.Length)
        {
            throw new HydraFormatException($"needs {count} byte(s) at {_position}, but the message ends at {_buffer.Length}");
        }

        var slice = _buffer.Slice(_position, count);
        _position += count;
        return slice;
    }

    private byte Byte() => Take(1)[0];

    private int Count(int width) => width switch
    {
        1 => Byte(),
        2 => BinaryPrimitives.ReadUInt16BigEndian(Take(2)),
        4 => checked((int)BinaryPrimitives.ReadUInt32BigEndian(Take(4))),
        _ => checked((int)BinaryPrimitives.ReadUInt64BigEndian(Take(8))),
    };

    private JsonNode? Value()
    {
        int at = _position;
        byte code = Byte();
        switch (code)
        {
            case HydraCode.Zero: return JsonValue.Create(0);
            case HydraCode.Null: return null;
            case HydraCode.True: return JsonValue.Create(true);
            case HydraCode.False: return JsonValue.Create(false);
            case HydraCode.WebSocket:
                Take(2);
                return Value();
            case HydraCode.Int8: return JsonValue.Create((sbyte)Byte());
            case HydraCode.UInt8: return JsonValue.Create(Byte());
            case HydraCode.Int16: return JsonValue.Create(BinaryPrimitives.ReadInt16BigEndian(Take(2)));
            case HydraCode.UInt16: return JsonValue.Create(BinaryPrimitives.ReadUInt16BigEndian(Take(2)));
            case HydraCode.Int32: return JsonValue.Create(BinaryPrimitives.ReadInt32BigEndian(Take(4)));
            case HydraCode.UInt32: return JsonValue.Create(BinaryPrimitives.ReadUInt32BigEndian(Take(4)));
            case HydraCode.Int64: return JsonValue.Create(BinaryPrimitives.ReadInt64BigEndian(Take(8)));
            case HydraCode.UInt64:
            case HydraCode.BigInt: return JsonValue.Create(BinaryPrimitives.ReadUInt64BigEndian(Take(8)));
            case HydraCode.Float: return JsonValue.Create((double)BinaryPrimitives.ReadSingleBigEndian(Take(4)));
            case HydraCode.Double:
                double d = BinaryPrimitives.ReadDoubleBigEndian(Take(8));
                return double.IsFinite(d) && d == Math.Floor(d) ? new JsonObject { ["_hydra_double"] = d } : JsonValue.Create(d);
            case HydraCode.Char8: return JsonValue.Create(Encoding.UTF8.GetString(Take(Count(1))));
            case HydraCode.Char16: return JsonValue.Create(Encoding.UTF8.GetString(Take(Count(2))));
            case HydraCode.Char32: return JsonValue.Create(Encoding.UTF8.GetString(Take(Count(4))));
            case HydraCode.Bytes8: return JsonValue.Create(Take(Count(1)).ToArray());
            case HydraCode.Bytes16: return JsonValue.Create(Take(Count(2)).ToArray());
            case HydraCode.Bytes32: return JsonValue.Create(Take(Count(4)).ToArray());
            case HydraCode.Date: return new JsonObject { ["_hydra_unix_date"] = BinaryPrimitives.ReadUInt32BigEndian(Take(4)) };
            case HydraCode.Array8: return Array(Count(1));
            case HydraCode.Array16: return Array(Count(2));
            case HydraCode.Array32: return Array(Count(4));
            case HydraCode.Array64: return Array(Count(8));
            case HydraCode.Map8: return Map(Count(1));
            case HydraCode.Map16: return Map(Count(2));
            case HydraCode.Map32: return Map(Count(4));
            case HydraCode.Map64: return Map(Count(8));
            case HydraCode.Compressed: return Compressed();
            case HydraCode.Localization:
                Value();
                Value();
                string language = Key(Value()) ?? "";
                return new JsonObject { ["localizations"] = new JsonObject { [language] = Value() } };
            case HydraCode.Calendar:
                var calendarDefault = Value();
                var calendarRendered = Value();
                return new JsonObject { ["_hydra_calendar"] = new JsonObject { ["default"] = calendarDefault, ["rendered"] = calendarRendered } };
            case HydraCode.FileReference:
                Value();
                Value();
                return new JsonObject { ["_customType"] = "hydra_reference", ["value"] = Value() };
            case HydraCode.StoreEnabled:
                Value();
                Value();
                return new JsonObject { ["_hydra_StoreEnabed"] = Value() };
            default:
                throw new HydraFormatException($"unknown type code 0x{code:X2} at {at}");
        }
    }

    private JsonArray Array(int count)
    {
        var array = new JsonArray();
        for (int i = 0; i < count; i++)
        {
            array.Add(Value());
        }

        return array;
    }

    // As in JavaScript: a key is its value as text, a null key's entry is dropped, and a repeated key keeps its first
    // position and takes the later value.
    private JsonObject Map(int count)
    {
        var map = new JsonObject();
        for (int i = 0; i < count; i++)
        {
            string? key = Key(Value());
            var value = Value();
            if (key is not null)
            {
                map[key] = value;
            }
        }

        return map;
    }

    private static string? Key(JsonNode? key) => key switch
    {
        null => null,
        JsonValue v when v.TryGetValue(out string? s) => s,
        _ => key.ToJsonString(),
    };

    // 0x67, an index byte (always 1 in practice), then a byte string holding the zlib (or gzip) data of one value.
    private JsonNode Compressed()
    {
        Byte();
        int width = Byte() switch
        {
            HydraCode.Bytes8 => 1,
            HydraCode.Bytes16 => 2,
            HydraCode.Bytes32 => 4,
            var other => throw new HydraFormatException($"compressed data must be a byte string, not 0x{other:X2}"),
        };
        var data = Take(Count(width));
        using var input = new MemoryStream(data.ToArray());
        using Stream inflate = data.Length > 1 && data[0] == 0x1F && data[1] == 0x8B
            ? new GZipStream(input, CompressionMode.Decompress)
            : new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        inflate.CopyTo(output);
        return new JsonObject { ["_hydra_compressed"] = Decode(output.ToArray()) };
    }
}

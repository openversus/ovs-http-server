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

    /// <summary>
    /// Where each item of the array under <paramref name="key"/> lies in a message that is a map (the TS server's
    /// /batch answer and its <c>responses</c>), so the items can be passed on byte for byte; null when the message is
    /// not a map or holds no array under that key.
    /// </summary>
    public static List<Range>? ArrayItems(ReadOnlySpan<byte> buffer, string key)
    {
        var decoder = new HydraDecoder(buffer);
        int entries = decoder.Header(HydraCode.Map8);
        for (int i = 0; i < entries; i++)
        {
            if (Key(decoder.Value()) != key)
            {
                decoder.Skip();
                continue;
            }

            int count = decoder.Header(HydraCode.Array8);
            if (count < 0)
            {
                return null;
            }

            var items = new List<Range>(count);
            for (int j = 0; j < count; j++)
            {
                int start = decoder._position;
                decoder.Skip();
                items.Add(start..decoder._position);
            }

            return items;
        }

        return null;
    }

    // Steps over one value without building it (nor inflating compressed data): ArrayItems only needs where it ends.
    // Follows Value() case for case.
    private void Skip()
    {
        int at = _position;
        byte code = Byte();
        switch (code)
        {
            case HydraCode.Zero or HydraCode.Null or HydraCode.True or HydraCode.False:
                return;
            case HydraCode.WebSocket:
                Take(2);
                Skip();
                return;
            case HydraCode.Int8 or HydraCode.UInt8:
                Take(1);
                return;
            case HydraCode.Int16 or HydraCode.UInt16:
                Take(2);
                return;
            case HydraCode.Int32 or HydraCode.UInt32 or HydraCode.Float or HydraCode.Date:
                Take(4);
                return;
            case HydraCode.Int64 or HydraCode.UInt64 or HydraCode.BigInt or HydraCode.Double:
                Take(8);
                return;
            case HydraCode.Char8 or HydraCode.Bytes8:
                Take(Count(1));
                return;
            case HydraCode.Char16 or HydraCode.Bytes16:
                Take(Count(2));
                return;
            case HydraCode.Char32 or HydraCode.Bytes32:
                Take(Count(4));
                return;
            case HydraCode.Array8 or HydraCode.Array16 or HydraCode.Array32 or HydraCode.Array64:
                for (int i = Count(1 << (code - HydraCode.Array8)); i > 0; i--)
                {
                    Skip();
                }

                return;
            case HydraCode.Map8 or HydraCode.Map16 or HydraCode.Map32 or HydraCode.Map64:
                for (int i = Count(1 << (code - HydraCode.Map8)); i > 0; i--)
                {
                    Skip();
                    Skip();
                }

                return;
            case HydraCode.Compressed:
                Byte();
                Skip();
                return;
            case HydraCode.Localization:
                Skip();
                Skip();
                Skip();
                Skip();
                return;
            case HydraCode.FileReference or HydraCode.StoreEnabled:
                Skip();
                Skip();
                Skip();
                return;
            case HydraCode.Calendar:
                Skip();
                Skip();
                return;
            default:
                throw new HydraFormatException($"unknown type code 0x{code:X2} at {at}");
        }
    }

    // The count after a map's (Map8 as first) or an array's (Array8) code of any width, or -1 for another code.
    private int Header(byte first)
    {
        int width = (Byte() - first) switch
        {
            0 => 1,
            1 => 2,
            2 => 4,
            3 => 8,
            _ => 0,
        };
        return width == 0 ? -1 : Count(width);
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

using System.Security.Cryptography;
using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Hydra;

namespace OpenVersus.Server.Core.Tests.Hydra;

/// <summary>
/// The Hydra codec against encoder-fixtures.json, made by running values through mvs-dump's encoder (the TS server's,
/// tools/hydra/gen_fixtures.mjs): the C# encoder writes the same bytes, the decoder reads them back to the same value,
/// and what mvs-dump refuses is refused. Plus the codes only the decoder needs, and malformed input.
/// </summary>
public sealed class HydraCodecTests
{
    private static readonly JsonObject s_fixtures = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Hydra", "encoder-fixtures.json")))!;

    public static IEnumerable<object[]> Fixtures() => s_fixtures.Select(f => new object[] { f.Key });

    // Huge strings are stored as { "$repeat": c, "count": n }.
    private static JsonNode? Expand(JsonNode? node) => node switch
    {
        JsonObject o when o.ContainsKey("$repeat") => JsonValue.Create(new string(o["$repeat"]!.GetValue<string>()[0], o["count"]!.GetValue<int>())),
        JsonObject o => new JsonObject(o.Select(kv => KeyValuePair.Create(kv.Key, Expand(kv.Value)))),
        JsonArray a => new JsonArray(a.Select(Expand).ToArray()),
        _ => node?.DeepClone(),
    };

    private static (JsonNode? Value, bool WebSocket, JsonObject Fixture) Load(string name)
    {
        var fixture = (JsonObject)s_fixtures[name]!;
        return (Expand(fixture["value"]), name.EndsWith("#websocket", StringComparison.Ordinal), fixture);
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void EncodesAsTheTsServerDoes(string name)
    {
        var (value, webSocket, fixture) = Load(name);
        if (fixture["error"] is not null)
        {
            Assert.Throws<HydraFormatException>(() => HydraEncoder.Encode(value, webSocket));
            return;
        }

        byte[] bytes = HydraEncoder.Encode(value, webSocket);
        if (fixture["decodeOnly"] is not null)
        {
            // Compressed: the zlib bytes may differ between builds; the value inside must not.
            Assert.True(JsonNode.DeepEquals(value, HydraDecoder.Decode(bytes)), "our compressed output does not decode to the value");
            return;
        }

        if (fixture["sha256"] is not null)
        {
            Assert.Equal(fixture["length"]!.GetValue<int>(), bytes.Length);
            Assert.Equal(fixture["sha256"]!.GetValue<string>(), Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
        else
        {
            Assert.Equal(fixture["hex"]!.GetValue<string>(), Convert.ToHexStringLower(bytes));
        }
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void DecodesTheTsServersBytesBackToTheSameValue(string name)
    {
        var (value, webSocket, fixture) = Load(name);
        if (fixture["hex"] is not { } hex)
        {
            return;
        }

        var decoded = HydraDecoder.Decode(Convert.FromHexString(hex.GetValue<string>()));
        if (fixture["decodeOnly"] is not null)
        {
            Assert.True(JsonNode.DeepEquals(value, decoded), $"decoded {decoded?.ToJsonString()}");
            return;
        }

        // What the decoder makes, the encoder writes back unchanged: the shapes agree.
        Assert.Equal(hex.GetValue<string>(), Convert.ToHexStringLower(HydraEncoder.Encode(decoded, webSocket)));
    }

    [Theory]
    [InlineData("10ff", -1)]
    [InlineData("12fffe", -2)]
    [InlineData("14fffffffd", -3)]
    [InlineData("00", 0)]
    public void ReadsSignedAndZeroCodesTheTsServerNeverWrites(string hex, long expected)
    {
        Assert.Equal(expected, Convert.ToInt64(HydraDecoder.Decode(Convert.FromHexString(hex))!.GetValue<object>()));
    }

    [Fact]
    public void ReadsTheOtherCodes()
    {
        Assert.Equal(1.5, Convert.ToDouble(HydraDecoder.Decode(Convert.FromHexString("203fc00000"))!.GetValue<object>()));
        Assert.Equal(new byte[] { 1, 2, 3 }, HydraDecoder.Decode(Convert.FromHexString("3303010203"))!.GetValue<byte[]>());
        Assert.Equal(ulong.MaxValue, HydraDecoder.Decode(Convert.FromHexString("17ffffffffffffffff"))!.GetValue<ulong>());
        Assert.Equal(long.MinValue, HydraDecoder.Decode(Convert.FromHexString("168000000000000000"))!.GetValue<long>());
        var array64 = (JsonArray)HydraDecoder.Decode(Convert.FromHexString("5300000000000000021101" + "01"))!;
        Assert.Equal(2, array64.Count);
        Assert.Null(array64[1]);
    }

    [Theory]
    [InlineData("""{ "a": 1, "_hydra_unix_date": 5 }""")]
    [InlineData("""{ "_hydra_double": 2, "b": true }""")]
    [InlineData("""{ "x": { "localizations": { "en": "a" }, "y": 1 } }""")]
    public void RefusesAWrapperNextToOtherKeys(string json)
    {
        // mvs-dump would overwrite the previous value and miscount the map; refusing is the only safe answer.
        Assert.Throws<HydraFormatException>(() => HydraEncoder.Encode(JsonNode.Parse(json)));
    }

    [Theory]
    [InlineData("18446744073709551616")]
    [InlineData("-9223372036854777856")]
    [InlineData("1e21")]
    [InlineData("-1e21")]
    public void RefusesAWholeNumberOutside64Bits(string json)
    {
        // mvs-dump's 64-bit writes refuse these; a cast would saturate to the nearest 64-bit value instead.
        Assert.Throws<HydraFormatException>(() => HydraEncoder.Encode(JsonNode.Parse(json)));
        Assert.Throws<HydraFormatException>(() => HydraEncoder.Encode(JsonValue.Create(double.Parse(json, System.Globalization.CultureInfo.InvariantCulture))));
    }

    [Fact]
    public void EncodesThe64BitBounds()
    {
        Assert.Equal("17ffffffffffffffff", Convert.ToHexStringLower(HydraEncoder.Encode(JsonNode.Parse("18446744073709551615"))));
        Assert.Equal("168000000000000000", Convert.ToHexStringLower(HydraEncoder.Encode(JsonNode.Parse("-9223372036854775808"))));
        Assert.Equal("168000000000000000", Convert.ToHexStringLower(HydraEncoder.Encode(JsonValue.Create(-9223372036854775808.0))));
    }

    [Theory]
    [InlineData("ee")]
    [InlineData("3005616263")]
    [InlineData("11")]
    [InlineData("110102")]
    public void RefusesBytesThatAreNotHydra(string hex)
    {
        Assert.Throws<HydraFormatException>(() => HydraDecoder.Decode(Convert.FromHexString(hex)));
    }
}

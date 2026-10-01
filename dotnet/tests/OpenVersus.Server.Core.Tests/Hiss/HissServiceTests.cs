using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Hiss;
using OpenVersus.Server.Core.Hosting;
using OpenVersus.Server.Core.Hydra;
using ZstdSharp;

namespace OpenVersus.Server.Core.Tests.Hiss;

/// <summary>
/// The hiss answer: the template's markers, the lists loadAssets.ts builds, the compressed sections' framing, and the
/// answer built once per CRC (tools/hiss/hiss_diff.mjs compares it with the TS server's). The cache tests use real Mongo
/// (a database of their own, dropped) and Redis: OVS_TEST_MONGO, OVS_TEST_REDIS, OVS_TEST_REDIS_USER, OVS_TEST_REDIS_PW.
/// </summary>
[Collection(RedisTestDatabase.Name)]
public sealed class HissServiceTests : IAsyncLifetime
{
    private const string TestMongoDb = "ovs_hiss_tests";
    private static readonly string? s_redis = Environment.GetEnvironmentVariable("OVS_TEST_REDIS");
    private static readonly string? s_mongo = Environment.GetEnvironmentVariable("OVS_TEST_MONGO");

    private WebApplication? _app;
    private IMongoClient? _seedMongo;

    private static bool Configured => !string.IsNullOrEmpty(s_redis) && !string.IsNullOrEmpty(s_mongo);

    private IHissService Hiss => _app!.Services.GetRequiredService<IHissService>();

    private IMongoDatabase Db => _seedMongo!.GetDatabase(TestMongoDb);

    private static BsonDocument Asset(string type, string? slug, string? character = null)
    {
        var asset = new BsonDocument { ["assetType"] = type, ["enabled"] = true };
        if (slug is not null)
        {
            asset["slug"] = slug;
        }

        if (character is not null)
        {
            asset["character_slug"] = character;
        }

        return asset;
    }

    [Fact]
    public void ListsAreBuiltAsLoadAssetsBuildsThem()
    {
        BsonDocument[] assets =
        [
            Asset("CharacterData", "character_a"), Asset("SkinData", "skin_b", "character_b"), Asset("CharacterData", "character_b"),
            Asset("SkinData", "skin_a", "character_a"), Asset("TauntData", "taunt_b", "character_b"), Asset("SkinData", "skin_loose"),
            Asset("CharacterData", null), Asset("CharacterData", "character_a"), Asset("EmoteData", "emote"),
        ];

        var values = HissService.Values(7, assets);

        Assert.Equal("[\"character_a\",\"skin_b\",\"character_b\",\"skin_a\",\"taunt_b\",\"skin_loose\",null,\"character_a\",\"emote\"]", values["{{assets:all}}"]!.ToJsonString());
        Assert.Equal("[\"character_a\",\"character_b\",null,\"character_a\"]", values["{{assets:CharacterData}}"]!.ToJsonString());
        Assert.Equal("[\"emote\"]", values["{{assets:EmoteData}}"]!.ToJsonString());
        Assert.Equal("[]", values["{{assets:MvsPerkHsda}}"]!.ToJsonString());
        // A character listed twice keeps its first place; a character without a slug is "undefined" and takes the
        // items without one (undefined === undefined).
        Assert.Equal("{\"character_a\":{\"Slugs\":[\"skin_a\"]},\"character_b\":{\"Slugs\":[\"skin_b\"]},\"undefined\":{\"Slugs\":[\"skin_loose\"]}}",
            values["{{skinsByCharacter}}"]!.ToJsonString());
        Assert.Equal("{\"character_a\":{\"Slugs\":[]},\"character_b\":{\"Slugs\":[\"taunt_b\"]},\"undefined\":{\"Slugs\":[]}}",
            values["{{tauntsByCharacter}}"]!.ToJsonString());
    }

    [Fact]
    public void EveryMarkerIsFilled()
    {
        var answer = HissService.Fill(HissService.Values(42, [Asset("CharacterData", "character_a"), Asset("SkinData", "skin_a", "character_a")]));

        Assert.DoesNotContain("{{", answer.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(42, answer["body"]!["Crc"]!.GetValue<double>());
        Assert.Equal(1, answer["body"]!["MatchmakingCrc"]!.GetValue<int>());
        var data = answer["body"]!["Data"]!.AsObject();
        Assert.Equal(20, data.Count);
        Assert.All(data, section => Assert.NotNull(section.Value!["_hydra_compressed"]));
    }

    [Fact]
    public void AMissingMarkerIsRefused()
    {
        var values = HissService.Values(1, []);
        values["{{notInTheTemplate}}"] = 1;

        Assert.Throws<InvalidOperationException>(() => HissService.Fill(values));
    }

    // The compressed value's length is written in 1, 2 or 4 bytes by size (mvs-dump: up to 255, up to 65,535, more).
    [Theory]
    [InlineData(100, HydraCode.Bytes8)]
    [InlineData(5_000, HydraCode.Bytes16)]
    [InlineData(200_000, HydraCode.Bytes32)]
    public void CompressedValuesAreFramedByTheirSize(int length, byte code)
    {
        // Random letters: little to compress, so the compressed size lands in the band.
        var random = new Random(length);
        string text = new([.. Enumerable.Range(0, length).Select(_ => (char)('a' + random.Next(26)))]);
        var value = new JsonObject { ["section"] = new JsonObject { ["_hydra_compressed"] = new JsonObject { ["text"] = text } } };

        byte[] bytes = HydraEncoder.Encode(value, compression: CompressionLevel.Optimal);

        int at = bytes.AsSpan().IndexOf(HydraCode.Compressed);
        Assert.Equal(1, bytes[at + 1]);
        Assert.Equal(code, bytes[at + 2]);
        // The length written is the zlib data's, to the end of the message (the value is the last one in it).
        int width = code == HydraCode.Bytes8 ? 1 : code == HydraCode.Bytes16 ? 2 : 4;
        long written = 0;
        for (int i = 0; i < width; i++)
        {
            written = (written << 8) | bytes[at + 3 + i];
        }

        Assert.Equal(bytes.Length - (at + 3 + width), written);
        Assert.Equal(code, written <= byte.MaxValue ? HydraCode.Bytes8 : written <= ushort.MaxValue ? HydraCode.Bytes16 : HydraCode.Bytes32);
        Assert.True(JsonNode.DeepEquals(value, HydraDecoder.Decode(bytes)), "the compressed value does not decode to itself");
    }

    [Theory]
    [InlineData("2026.10.1", "2026.10.1", true)]
    [InlineData("2026.10.2", "2026.10.1", true)]
    [InlineData("2026.11.1", "2026.10.30", true)]
    [InlineData("2026.9.30", "2026.10.1", false)]
    [InlineData("2026.10.1", "", false)]
    [InlineData("2026.10.1", "not a version", false)]
    [InlineData("", "2026.10.1", false)]
    [InlineData("legacy", "2026.10.1", false)]
    public void ZstdGoesToClientsFromTheMinimumOn(string client, string minimum, bool zstd)
    {
        Assert.Equal(zstd, HissService.ReadsZstd(client, minimum));
    }

    // The contract with the client's decoder (HissZstd): one frame, the magic, the content size (so the window is the
    // section's size), a window the client accepts, and a checksum.
    [Theory]
    [InlineData(2)]
    [InlineData(94)]
    [InlineData(64_828)]
    [InlineData(1_563_088)]
    public void ZstdSectionsAreFramesTheClientReads(int size)
    {
        var random = new Random(size);
        byte[] section = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, size / 5 + 1).Select(i => $"s{random.Next(500):D3},")))[..size];

        byte[] frame = HissZstd.Compress(section);

        Assert.Equal([0x28, 0xB5, 0x2F, 0xFD], frame[..4]);
        var (checksum, contentSize, window) = FrameHeader(frame);
        Assert.True(checksum, "no checksum");
        Assert.Equal((ulong)size, contentSize);
        Assert.True(window <= HissZstd.ClientWindowLimit, $"window {window}");
        Assert.Equal(section, Decompress(frame));
    }

    // A zstd frame header (RFC 8878, 3.1.1.1): the checksum flag, the content size if stated, and the window size.
    private static (bool Checksum, ulong? ContentSize, ulong Window) FrameHeader(byte[] frame)
    {
        byte descriptor = frame[4];
        int sizeFlag = descriptor >> 6, dictionaryFlag = descriptor & 3;
        bool singleSegment = (descriptor & 0x20) != 0, checksum = (descriptor & 0x04) != 0;
        int at = 5;
        ulong window = 0;
        if (!singleSegment)
        {
            int exponent = frame[at] >> 3, mantissa = frame[at] & 7;
            ulong windowBase = 1UL << (10 + exponent);
            window = windowBase + windowBase / 8 * (ulong)mantissa;
            at++;
        }

        at += dictionaryFlag switch { 0 => 0, 1 => 1, 2 => 2, _ => 4 };
        int sizeBytes = sizeFlag switch { 0 => singleSegment ? 1 : 0, 1 => 2, 2 => 4, _ => 8 };
        ulong? contentSize = null;
        if (sizeBytes > 0)
        {
            ulong value = 0;
            for (int b = sizeBytes - 1; b >= 0; b--)
            {
                value = (value << 8) | frame[at + b];
            }

            contentSize = sizeBytes == 2 ? value + 256 : value;
        }

        // A single-segment frame's window is its content size.
        return (checksum, contentSize, singleSegment ? contentSize ?? 0 : window);
    }

    // Compressed values (0x67, index 1, a byte string) whose data is a zstd frame.
    private static int ZstdSections(byte[] hydra)
    {
        int found = 0;
        for (int i = 0; i + 7 < hydra.Length; i++)
        {
            int width = hydra[i] == HydraCode.Compressed && hydra[i + 1] == 1
                ? hydra[i + 2] switch { HydraCode.Bytes8 => 1, HydraCode.Bytes16 => 2, HydraCode.Bytes32 => 4, _ => 0 }
                : 0;
            if (width == 0)
            {
                continue;
            }

            long length = 0;
            for (int b = 0; b < width; b++)
            {
                length = (length << 8) | hydra[i + 3 + b];
            }

            int data = i + 3 + width;
            if (data + 4 <= hydra.Length && hydra.AsSpan(data, 4).SequenceEqual((byte[])[0x28, 0xB5, 0x2F, 0xFD]))
            {
                found++;
                i = data + (int)length - 1;
            }
        }

        return found;
    }

    private static byte[] Decompress(byte[] frame)
    {
        using var decompressor = new Decompressor();
        return decompressor.Unwrap(frame).ToArray();
    }

    [Fact]
    public async Task TheAnswerIsBuiltOncePerCrc()
    {
        if (!Configured)
        {
            return;
        }

        await Db.GetCollection<BsonDocument>("dataassets").InsertManyAsync([Asset("CharacterData", "character_a"), Asset("SkinData", "skin_a", "character_a")]);

        // No config document: the TS server's in-memory default.
        var first = await Hiss.AnswerAsync(CancellationToken.None);
        Assert.Equal(HissService.DefaultCrc, first.Crc);
        Assert.Same(first, await Hiss.AnswerAsync(CancellationToken.None));

        // An asset sync: the asset changes and the CRC is bumped.
        await Db.GetCollection<BsonDocument>("dataassets").InsertOneAsync(Asset("SkinData", "skin_new", "character_a"));
        await Db.GetCollection<BsonDocument>("config").InsertOneAsync(new BsonDocument("CRC", 2));
        var second = await Hiss.AnswerAsync(CancellationToken.None);
        Assert.Equal(2, second.Crc);
        Assert.NotSame(first, second);
        Assert.Contains("skin_new", second.Json, StringComparison.Ordinal);
        Assert.DoesNotContain("skin_new", first.Json, StringComparison.Ordinal);
        Assert.Equal("2", HydraDecoder.Decode(second.Hydra)!["body"]!["Crc"]!.ToJsonString());
        Assert.Same(second, await Hiss.AnswerAsync(CancellationToken.None));

        // The zstd encoding holds the same answer, and is smaller.
        byte[]? zstd = await second.Zstd;
        Assert.NotNull(zstd);
        Assert.Same(zstd, second.ZstdHydra);
        Assert.True(JsonNode.DeepEquals(HydraDecoder.Decode(second.Hydra), HydraDecoder.Decode(zstd)), "the zstd answer differs from the zlib one");
        Assert.True(zstd!.Length < second.Hydra.Length, $"zstd {zstd.Length} vs zlib {second.Hydra.Length}");
        Assert.Equal(20, ZstdSections(zstd));
        Assert.Equal(0, ZstdSections(second.Hydra));
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public async Task InitializeAsync()
    {
        if (!Configured)
        {
            return;
        }

        string[] parts = s_redis!.Split(':');
        var mongoUrl = new MongoUrlBuilder(s_mongo) { DatabaseName = TestMongoDb };
        _seedMongo = new MongoClient(mongoUrl.ToMongoUrl());
        await _seedMongo.DropDatabaseAsync(TestMongoDb);

        var builder = OpenVersusHost.CreateBuilder(new ServiceDefinition("hisstest", "TEST_PORT", 1, 1),
        [
            $"--TEST_PORT={FreePort()}", $"--Control:Port={FreePort()}", "--Control:Socket=off",
            $"--REDIS={parts[0]}", $"--REDIS_PORT={(parts.Length > 1 ? parts[1] : "6379")}",
            $"--REDIS_USERNAME={Environment.GetEnvironmentVariable("OVS_TEST_REDIS_USER") ?? ""}",
            $"--REDIS_PW={Environment.GetEnvironmentVariable("OVS_TEST_REDIS_PW") ?? ""}",
            "--REDIS_DB=15", $"--MONGODB_URI={mongoUrl.ToMongoUrl()}",
        ]);
        builder.AddHiss();
        _app = builder.Build();
        _app.UseOpenVersus();
        await _app.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        if (_seedMongo is not null)
        {
            await _seedMongo.DropDatabaseAsync(TestMongoDb);
        }
    }
}

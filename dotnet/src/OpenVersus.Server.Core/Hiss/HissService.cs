using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Assets;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Hydra;

namespace OpenVersus.Server.Core.Hiss;

// GET and PUT /ssc/invoke/hiss_amalgamation, ported from the TS server's generate_hiss (handlers/hiss_amalgation_get.ts,
// branch infinity-war): the game's configuration (hiss-amalgamation.json, generated from the TS literal and its
// src/data constants by tools/access/gen_templates.mjs) with the CRC and the lists built from the enabled data assets
// filled in as loadAssets.ts builds them. As there, the client's Crc is not read: the whole answer is always sent.
//
// The answer changes only with the data assets, and syncing an asset (the TS server's POST /syncAsset) bumps the CRC, so
// it is built once per CRC. The CRC is read from the config collection on every request, so every replica sees a sync
// (the TS server reads it once, at startup, and only the replica that took the sync sees it). A build reads the assets
// after the CRC: an answer is never older than its CRC.
//
// Built once, the compressed sections take zlib's optimal level (about 408 KB, against about 514 KB at mvs-dump's fastest
// level; smallest size gave about 410 KB on this data, and takes longer). The game inflates the sections itself (zlib; the client has no other decompressor for them), so their bytes
// need not be the TS server's; what they hold is.
//
// Mongo, read     config (the first document's CRC), dataassets (enabled)

public interface IHissService
{
    /// <summary>The answer for the current CRC.</summary>
    Task<HissAnswer> AnswerAsync(CancellationToken ct);
}

/// <summary>The hiss answer for one CRC: its Hydra encoding, and its JSON text (made on first use).</summary>
public sealed class HissAnswer(double crc, byte[] hydra, Func<string> json)
{
    private readonly Lazy<string> _json = new(json);

    public double Crc { get; } = crc;

    public byte[] Hydra { get; } = hydra;

    /// <summary>As Express writes it (JSON.stringify): the sections stay plain objects under <c>_hydra_compressed</c>.</summary>
    public string Json => _json.Value;
}

internal sealed class HissService(IServiceProvider services, ILogger<HissService> log) : IHissService
{
    // data/config.ts: the CRC the TS server answers with while the config collection has no document.
    internal const double DefaultCrc = 1267552956;
    private const string Template = "OpenVersus.Server.Core.Hiss.hiss-amalgamation.json";

    private readonly Lock _gate = new();
    private (double Crc, Task<HissAnswer> Build)? _current;

    public async Task<HissAnswer> AnswerAsync(CancellationToken ct)
    {
        var mongo = services.GetService<IMongoDatabase>() ?? throw new InvalidOperationException("this service has no Mongo (MONGODB_URI)");
        double crc = await CrcAsync(mongo, ct);
        Task<HissAnswer> build;
        lock (_gate)
        {
            // A build is shared by every request waiting on it, so it does not take their cancellation; a failed one is
            // tried again by the next request.
            if (_current is not { } current || current.Crc != crc || current.Build.IsFaulted || current.Build.IsCanceled)
            {
                _current = (crc, Task.Run(() => BuildAsync(mongo, crc)));
            }

            build = _current.Value.Build;
        }

        return await build.WaitAsync(ct);
    }

    // LoadConfig: the first document's CRC.
    private static async Task<double> CrcAsync(IMongoDatabase mongo, CancellationToken ct)
    {
        var config = await mongo.GetCollection<BsonDocument>("config").Find(FilterDefinition<BsonDocument>.Empty).Limit(1).FirstOrDefaultAsync(ct);
        return config?.GetValue("CRC", BsonNull.Value) is { IsNumeric: true } value ? value.ToDouble() : DefaultCrc;
    }

    private async Task<HissAnswer> BuildAsync(IMongoDatabase mongo, double crc)
    {
        long started = Stopwatch.GetTimestamp();
        var assets = await DataAssets.EnabledAsync(mongo, CancellationToken.None);
        var values = Values(crc, assets);
        var answer = Fill(values);
        byte[] hydra = HydraEncoder.Encode(answer, compression: CompressionLevel.Optimal);
        log.LogInformation("Built the hiss answer for CRC {Crc}: {Assets} data assets, {Bytes} bytes, {Ms:F0} ms",
            crc, assets.Count, hydra.Length, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return new HissAnswer(crc, hydra, () => Js.Stringify(Fill(values)));
    }

    /// <summary>The markers' values: the CRC, and loadAssets.ts's lists (getAssetsByType, getAllSkinsByChar, ...).</summary>
    internal static Dictionary<string, JsonNode?> Values(double crc, IReadOnlyList<BsonDocument> assets)
    {
        var values = new Dictionary<string, JsonNode?>
        {
            ["{{crc}}"] = JsonValue.Create(crc),
            ["{{assets:all}}"] = Slugs(assets),
            ["{{skinsByCharacter}}"] = ByCharacter(assets, "SkinData"),
            ["{{tauntsByCharacter}}"] = ByCharacter(assets, "TauntData"),
        };
        foreach (string type in (string[])["CharacterData", "EmoteData", "BannerData", "RingOutVfxData", "ProfileIconData", "AnnouncerPackData",
                     "StatTrackingBundleData", "MvsGemHsda", "MvsPerkHsda"])
        {
            values[$"{{{{assets:{type}}}}}"] = Slugs(OfType(assets, type));
        }

        return values;
    }

    private static IEnumerable<BsonDocument> OfType(IEnumerable<BsonDocument> assets, string type) =>
        assets.Where(a => DataAssets.Str(a, "assetType") == type);

    // .map((a) => a.slug)
    private static JsonArray Slugs(IEnumerable<BsonDocument> assets) => new([.. assets.Select(a => (JsonNode?)JsonValue.Create(DataAssets.Str(a, "slug")))]);

    // getAllSkinsByChar / getAllTauntsByChar: for each character, in asset order, { Slugs: [its items' slugs] }. A
    // character listed twice keeps its first place and its last list, as an object key assigned twice does.
    private static JsonObject ByCharacter(IReadOnlyList<BsonDocument> assets, string type)
    {
        var items = OfType(assets, type).ToList();
        var byCharacter = new JsonObject();
        foreach (var character in OfType(assets, "CharacterData"))
        {
            string? slug = DataAssets.Str(character, "slug");
            byCharacter[slug ?? "undefined"] = new JsonObject { ["Slugs"] = Slugs(items.Where(i => DataAssets.Str(i, "character_slug") == slug)) };
        }

        return byCharacter;
    }

    /// <summary>The template with every marker replaced by its value.</summary>
    internal static JsonNode Fill(IReadOnlyDictionary<string, JsonNode?> values)
    {
        using var stream = typeof(HissService).Assembly.GetManifestResourceStream(Template)
            ?? throw new InvalidOperationException("hiss-amalgamation.json is not embedded");
        var answer = JsonNode.Parse(stream)!;
        int filled = Fill(answer, values);
        if (filled != values.Count)
        {
            throw new InvalidOperationException($"hiss-amalgamation.json has {filled} of the {values.Count} markers; regenerate it (tools/access/gen_templates.mjs)");
        }

        return answer;
    }

    private static int Fill(JsonNode? node, IReadOnlyDictionary<string, JsonNode?> values)
    {
        int filled = 0;
        switch (node)
        {
            case JsonObject obj:
                foreach (string key in obj.Select(kv => kv.Key).ToList())
                {
                    if (obj[key] is JsonValue v && v.TryGetValue(out string? text) && values.TryGetValue(text, out var value))
                    {
                        obj[key] = value?.DeepClone();
                        filled++;
                    }
                    else
                    {
                        filled += Fill(obj[key], values);
                    }
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    filled += Fill(item, values);
                }

                break;
        }

        return filled;
    }
}

public static class HissHosting
{
    public static WebApplicationBuilder AddHiss(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<IHissService, HissService>();
        return builder;
    }
}

using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Assets;
using OpenVersus.Server.Core.Clients;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Hydra;
using OpenVersus.Server.Core.RewardTracks;
using OpenVersus.Server.Core.Rifts;
using OpenVersus.Server.Core.Settings;

namespace OpenVersus.Server.Core.Hiss;

// GET and PUT /ssc/invoke/hiss_amalgamation, ported from the TS server's generate_hiss (handlers/hiss_amalgation_get.ts,
// branch infinity-war): the game's configuration (hiss-amalgamation.json, generated from the TS literal and its
// src/data constants by tools/access/gen_templates.mjs) with the CRC and the lists built from the enabled data assets
// filled in as loadAssets.ts builds them. As there, the client's Crc is not read: the whole answer is always sent.
//
// The answer changes only with the data assets, and syncing an asset (the TS server's POST /syncAsset) bumps the CRC, so
// it is built once per CRC. The Crc the game sees is the config CRC plus Hiss:ContentRevision (the TS server's
// getCurrentCRC: CRC + HISS_CONTENT_REVISION), the knob for content changes that sync no asset. The CRC is read from the config collection on every request, so every replica sees a sync
// (the TS server reads it once, at startup, and only the replica that took the sync sees it). A build reads the assets
// after the CRC: an answer is never older than its CRC.
//
// Built once, the compressed sections take zlib's optimal level (about 408 KB, against about 514 KB at mvs-dump's fastest
// level; smallest size gave about 410 KB on this data, and takes longer). The game unpacks the sections itself, so their
// bytes need not be the TS server's; what they hold is. The stock game reads only zlib; the OpenVersus client from
// Hiss:ZstdMinimumVersion on also reads zstd (its HydraZstd hook), and gets zstd sections (about 250 KB, HissZstd). The
// zstd encoding is made after the zlib one, in the background, so a login never waits for it: until it is ready, every
// client gets zlib. Both are made at startup (HissWarmup).
//
// Mongo, read     config (the first document's CRC), dataassets (enabled)
// Redis, read     what IClientUpdateGate.ForRequestAsync reads (the client's version), only while Hiss:ZstdMinimumVersion is set

/// <summary>The hiss settings.</summary>
public sealed class HissSettings
{
    [Description("The oldest OpenVersus client version that reads zstd sections (its HydraZstd hook), such as 2026.10.01.1; those clients get the smaller zstd answer. From 2026.10.08.14 on a client says itself whether its hook took (the X-OVS-Zstd header), and only that decides: a build the hook misses (the Epic Games Store one, 2026-10-08) gets zlib. Empty: every client gets zlib.")]
    public string ZstdMinimumVersion { get; set; } = "";

    [Description("Added to the config document's CRC in the Crc the game is answered (the hiss and every TS catch-all answer): bump it when the hiss content changes without an asset sync, so no client reuses a cached catalog. The TS server's HISS_CONTENT_REVISION (15 at the port's reference).")]
    [System.ComponentModel.DataAnnotations.Range(0, int.MaxValue)]
    public int ContentRevision { get; set; } = 15;

    [Description("The MatchmakingCrc the game is answered (the hiss and every TS catch-all answer): bump it when the queue or game-mode catalog changes, so clients drop their cached matchmaking configuration. The TS server's MATCHMAKING_CRC (2 at the port's reference).")]
    [System.ComponentModel.DataAnnotations.Range(0, int.MaxValue)]
    public int MatchmakingCrc { get; set; } = 2;
}

public interface IHissService
{
    /// <summary>The answer for the current CRC.</summary>
    Task<HissAnswer> AnswerAsync(CancellationToken ct);

    /// <summary>Whether zstd sections may be sent to anyone (Hiss:ZstdMinimumVersion is a version).</summary>
    bool ZstdEnabled { get; }

    /// <summary>Whether a client of <paramref name="clientVersion"/> reads zstd sections: at least Hiss:ZstdMinimumVersion.</summary>
    /// <summary>Whether this client gets zstd sections: its version and, from 2026.10.08.14 on, its X-OVS-Zstd header.</summary>
    bool ReadsZstd(string clientVersion, string? zstdHeader);
}

/// <summary>
/// The hiss answer for one CRC: its Hydra encoding with zlib sections, the same with zstd sections once that is made,
/// and its JSON text (made on first use).
/// </summary>
public sealed class HissAnswer(double crc, byte[] hydra, Task<byte[]?> zstd, Func<string> json)
{
    private readonly Lazy<string> _json = new(json);

    public double Crc { get; } = crc;

    /// <summary>The Hydra encoding, its sections in zlib: for every client.</summary>
    public byte[] Hydra { get; } = hydra;

    /// <summary>The Hydra encoding with zstd sections, for clients that read them; null until it is made (or if it failed).</summary>
    public byte[]? ZstdHydra => Zstd.IsCompletedSuccessfully ? Zstd.Result : null;

    /// <summary>Making <see cref="ZstdHydra"/>.</summary>
    internal Task<byte[]?> Zstd { get; } = zstd;

    /// <summary>As Express writes it (JSON.stringify): the sections stay plain objects under <c>_hydra_compressed</c>.</summary>
    public string Json => _json.Value;
}

internal sealed class HissService(IServiceProvider services, IOptionsMonitor<HissSettings> settings, ILogger<HissService> log) : IHissService
{
    // data/config.ts: the CRC the TS server answers with while the config collection has no document.
    internal const double DefaultCrc = 1267552956;
    private const string Template = "OpenVersus.Server.Core.Hiss.hiss-amalgamation.json";

    private readonly Lock _gate = new();
    private (double Crc, int MatchmakingCrc, string FighterPass, Task<HissAnswer> Build)? _current;

    public async Task<HissAnswer> AnswerAsync(CancellationToken ct)
    {
        var mongo = services.GetService<IMongoDatabase>() ?? throw new InvalidOperationException("this service has no Mongo (MONGODB_URI)");
        double crc = await CurrentCrcAsync(services, ct);
        int matchmakingCrc = MatchmakingCrcOf(services);
        var fighterPass = FighterPass.Current;
        string fighterPassKey = FighterPass.Key(fighterPass);
        Task<HissAnswer> build;
        lock (_gate)
        {
            // A build is shared by every request waiting on it, so it does not take their cancellation; a failed one is
            // tried again by the next request.
            if (_current is not { } current || current.Crc != crc || current.MatchmakingCrc != matchmakingCrc || current.FighterPass != fighterPassKey || current.Build.IsFaulted || current.Build.IsCanceled)
            {
                _current = (crc, matchmakingCrc, fighterPassKey, Task.Run(() => BuildAsync(mongo, crc, matchmakingCrc, fighterPass)));
            }

            build = _current.Value.Build;
        }

        return await build.WaitAsync(ct);
    }

    public bool ZstdEnabled => ClientVersions.Parts(Js.Trim(settings.CurrentValue.ZstdMinimumVersion)) is not null;

    public bool ReadsZstd(string clientVersion, string? zstdHeader) => ReadsZstd(clientVersion, settings.CurrentValue.ZstdMinimumVersion, zstdHeader);

    /// <summary>
    /// With a minimum configured (else never): a client from <see cref="ZstdHeaderSince"/> on reads zstd exactly when it
    /// says so (<paramref name="zstdHeader"/> "1": its hook took on this build); an older one when its version is at
    /// least <paramref name="minimum"/>, compared as versions (2026.10.1 is above 2026.9.30). A client that states no
    /// version: no.
    /// </summary>
    internal static bool ReadsZstd(string clientVersion, string minimum, string? zstdHeader)
    {
        string configured = Js.Trim(minimum);
        if (ClientVersions.Parts(configured) is null || ClientVersions.Parts(clientVersion) is null)
        {
            return false;
        }

        if (ClientVersions.Compare(clientVersion, HissZstd.HeaderSince) >= 0)
        {
            return Js.Trim(zstdHeader ?? "") == "1";
        }

        return ClientVersions.Compare(clientVersion, configured) >= 0;
    }

    /// <summary>The MatchmakingCrc the game is answered (<see cref="HissSettings.MatchmakingCrc"/>): in the hiss and every catch-all answer.</summary>
    public static int MatchmakingCrcOf(IServiceProvider services) =>
        services.GetService<IOptionsMonitor<HissSettings>>()?.CurrentValue.MatchmakingCrc ?? new HissSettings().MatchmakingCrc;

    /// <summary>
    /// The Crc the game is answered (the TS server's getCurrentCRC): the config document's CRC (the default without
    /// Mongo or a document) plus <see cref="HissSettings.ContentRevision"/>.
    /// </summary>
    public static async Task<double> CurrentCrcAsync(IServiceProvider services, CancellationToken ct)
    {
        var mongo = services.GetService<IMongoDatabase>();
        double crc = mongo is null ? DefaultCrc : await CrcAsync(mongo, ct);
        return crc + (services.GetService<IOptionsMonitor<HissSettings>>()?.CurrentValue.ContentRevision ?? new HissSettings().ContentRevision);
    }

    // LoadConfig: the first document's CRC.
    /// <summary>The config document's CRC, else the default: the part of the game's Crc an asset sync bumps.</summary>
    internal static async Task<double> CrcAsync(IMongoDatabase mongo, CancellationToken ct)
    {
        var config = await mongo.GetCollection<BsonDocument>("config").Find(FilterDefinition<BsonDocument>.Empty).Limit(1).FirstOrDefaultAsync(ct);
        return config?.GetValue("CRC", BsonNull.Value) is { IsNumeric: true } value ? value.ToDouble() : DefaultCrc;
    }

    private async Task<HissAnswer> BuildAsync(IMongoDatabase mongo, double crc, int matchmakingCrc, FighterPassSettings fighterPass)
    {
        long started = Stopwatch.GetTimestamp();
        var assets = await DataAssets.EnabledAsync(mongo, CancellationToken.None);
        var values = Values(crc, matchmakingCrc, assets);
        var answer = Fill(values);
        RiftCatalog.ExtendEndTimes(answer["body"]?["Data"]?["rift-config"]?["_hydra_compressed"] as JsonObject);
        ExtendFighterPasses(answer, fighterPass);
        byte[] hydra = HydraEncoder.Encode(answer, compression: CompressionLevel.Optimal);
        log.LogInformation("Built the hiss answer for CRC {Crc}: {Assets} data assets, {Bytes} bytes, {Ms:F0} ms",
            crc, assets.Count, hydra.Length, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return new HissAnswer(crc, hydra, Task.Run(() => BuildZstd(answer, crc)), () =>
        {
            var json = Fill(values);
            RiftCatalog.ExtendEndTimes(json["body"]?["Data"]?["rift-config"]?["_hydra_compressed"] as JsonObject);
            ExtendFighterPasses(json, fighterPass);
            return Js.Stringify(json);
        });
    }

    // The fighter tracks as FighterPass:ExtraTiers extends them, as the server's own tables (HissTables) have them.
    internal static void ExtendFighterPasses(JsonNode answer, FighterPassSettings fighterPass)
    {
        if (fighterPass.ExtraTiers > 0 && answer["body"]?["Data"]?["milestone-reward-tracks"] is JsonObject section && section["_hydra_compressed"] is JsonObject tracks)
        {
            section["_hydra_compressed"] = FighterPass.Extend(tracks, fighterPass);
        }
    }

    // The same answer with zstd sections. A failure leaves every client on zlib.
    private byte[]? BuildZstd(JsonNode answer, double crc)
    {
        long started = Stopwatch.GetTimestamp();
        try
        {
            byte[] hydra = HydraEncoder.Encode(answer, compressor: HissZstd.Compress);
            log.LogInformation("Built the zstd hiss answer for CRC {Crc}: {Bytes} bytes, {Ms:F0} ms", crc, hydra.Length, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return hydra;
        }
        catch (Exception e)
        {
            log.LogError("The zstd hiss answer for CRC {Crc} failed; every client gets zlib: {Error}", crc, e.Message);
            return null;
        }
    }

    /// <summary>The markers' values: the CRCs, and loadAssets.ts's lists (getAssetsByType, getAllSkinsByChar, ...).</summary>
    internal static Dictionary<string, JsonNode?> Values(double crc, int matchmakingCrc, IReadOnlyList<BsonDocument> assets)
    {
        var values = new Dictionary<string, JsonNode?>
        {
            ["{{crc}}"] = JsonValue.Create(crc),
            ["{{matchmaking_crc}}"] = JsonValue.Create(matchmakingCrc),
            // OwnedByDefaultInventoryItems: End Game's restricted items are owned once paid (Ownership), not by default.
            ["{{assets:all}}"] = Slugs(assets.Where(a => !Inventory.Ownership.IsRestricted(DataAssets.Str(a, "slug")))),
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

// Builds the answer (both encodings) at startup, in the background, so the first login does not wait for it and a client
// that reads zstd gets zstd from the first login on. Startup does not wait for it; an answer still being built is
// shared with any request that comes in meanwhile. A later CRC is built by the first request that sees it.
internal sealed class HissWarmup(IHissService hiss, IServiceProvider services, ILogger<HissWarmup> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (services.GetService<IMongoDatabase>() is null)
        {
            return;
        }

        try
        {
            var answer = await hiss.AnswerAsync(ct);
            await answer.Zstd.WaitAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning("The hiss answer could not be built at startup; the first request builds it: {Error}", e.Message);
        }
    }
}

public static class HissHosting
{
    public static WebApplicationBuilder AddHiss(this WebApplicationBuilder builder)
    {
        // Every game service registers Hiss:* too (GameHttpHost: the catch-all answers carry the Crc); registering
        // twice is harmless, and a host with the hiss alone still binds its settings.
        builder.AddSetting<HissSettings>("Hiss");
        builder.Services.AddSingleton<IHissService, HissService>();
        builder.Services.AddHostedService<HissWarmup>();
        return builder;
    }
}

/// <summary>The TS server's answer to an SSC call it does not implement (its catch-all).</summary>
public static class TsCatchAll
{
    /// <summary>{Crc (<see cref="HissService.CurrentCrcAsync"/>), MatchmakingCrc (<see cref="HissService.MatchmakingCrcOf"/>)}, return_code 200.</summary>
    public static async Task<JsonObject> AnswerAsync(IServiceProvider services, CancellationToken ct) => new()
    {
        ["body"] = new JsonObject { ["Crc"] = await HissService.CurrentCrcAsync(services, ct), ["MatchmakingCrc"] = HissService.MatchmakingCrcOf(services) },
        ["metadata"] = null,
        ["return_code"] = 200,
    };
}

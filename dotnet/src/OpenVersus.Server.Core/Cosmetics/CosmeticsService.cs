using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Assets;
using OpenVersus.Server.Core.Compat;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Cosmetics;

// GET /ssc/invoke/get_equipped_cosmetics, ported from the TS server's handleSsc_invoke_get_equipped_cosmetics
// (handlers/ssc.ts) and getEquippedCosmetics / mergeCosmetics (services/cosmeticsService.ts), branch infinity-war.
//
// Redis, read     player:{id}:cosmetics (JSON): when there, it is the answer, with a default filled in for a missing
//                 Banner ("default_banner"), RingoutVfx, StatTrackers or Taunts (not written back: there, the check that
//                 would save compares the object with itself)
// Mongo, read     cosmetics {_id: ObjectId(id)}; dataassets (CharacterData, TauntData; see DataAssets)
// Mongo, written  cosmetics insert for a player with none: _id, account_id (the player's ObjectId), the schema's defaults
//                 in its order (Taunts, AnnouncerPack, Banner, StatTrackers, RingoutVfx, Gems), __v 0
// Redis, written  player:{id}:cosmetics (no TTL) after a Mongo read: the document with a taunt entry for every character
//                 (its own, else the character's first taunt), as JSON.stringify writes it (_id, account_id as hex, __v)
//
// The answer carries the six fields WB's own answers carried, in WB's order (Taunts, Banner, RingoutVfx, AnnouncerPack,
// StatTrackers, Gems: live exports of May 2025 and the client's own cache agree). The TS answer also carries the stored
// document's _id (after a Mongo read as the ObjectId's twelve bytes, {buffer: {...}}), account_id and __v, which WB never
// sent. The Redis value keeps them: TS services read it.
//
// Unlike there: a session that resolves to no player falls back to the token's id, and one with no usable id gets the
// defaults without anything written (TS throws and never answers).

public interface ICosmeticsService
{
    /// <summary>The player's equipped cosmetics as the TS server keeps them (stored fields included).</summary>
    Task<JsonObject> EquippedAsync(string accountId, CancellationToken ct);
}

internal sealed class CosmeticsService(IServiceProvider services, ILogger<CosmeticsService> log) : ICosmeticsService
{
    private static readonly JsonObject s_defaultTaunts = Load("cosmetics-default-taunts");

    private static JsonObject StatTrackerDefault() => new()
    {
        ["StatTrackerSlots"] = new JsonArray("stat_tracking_bundle_default", "stat_tracking_bundle_default", "stat_tracking_bundle_default"),
    };

    public async Task<JsonObject> EquippedAsync(string accountId, CancellationToken ct)
    {
        var mongo = services.GetService<IMongoDatabase>() ?? throw new InvalidOperationException("this service has no Mongo (MONGODB_URI)");
        if (!ObjectId.TryParse(accountId, out var id))
        {
            log.LogError("Invalid accountId provided to getEquippedCosmetics: answering the defaults");
            return await MergeAsync(mongo, Defaults(), ct);
        }

        var redis = services.GetService<IConnectionMultiplexer>()?.GetDatabase() ?? throw new InvalidOperationException("this service has no Redis (REDIS)");
        string key = $"player:{accountId}:cosmetics";
        var cached = await redis.StringGetAsync(key);
        if (cached.HasValue && cached.ToString().Length > 0 && Js.Parse(cached.ToString()) is JsonObject stored)
        {
            if (!Truthy(stored["Banner"]))
            {
                stored["Banner"] = "default_banner";
            }

            if (!Truthy(stored["RingoutVfx"]))
            {
                stored["RingoutVfx"] = "ring_out_vfx_default";
            }

            if (!Truthy(stored["StatTrackers"]))
            {
                stored["StatTrackers"] = StatTrackerDefault();
            }

            if (!Truthy(stored["Taunts"]))
            {
                stored["Taunts"] = s_defaultTaunts.DeepClone();
            }

            return stored;
        }

        var collection = mongo.GetCollection<BsonDocument>("cosmetics");
        var doc = await collection.Find(new BsonDocument("_id", id)).FirstOrDefaultAsync(ct);
        if (doc is null)
        {
            // new CosmeticsModel().toObject(), then create({ ...that, _id: accountId, account_id: accountId }).
            // Stored in the schema's order: _id, account_id, the defaults, __v.
            var created = new BsonDocument { { "_id", id }, { "account_id", id } };
            foreach (var (name, value) in Defaults())
            {
                created[name] = BsonDocument.Parse($"{{\"v\":{Js.Stringify(value)}}}")["v"];
            }

            created["__v"] = 0;
            try
            {
                await collection.InsertOneAsync(created, cancellationToken: ct);
            }
            catch (MongoWriteException e)
            {
                log.LogError("Error creating cosmetics for account {Account}: {Error}", accountId, e.Message);
            }

            doc = await collection.Find(new BsonDocument("_id", id)).FirstOrDefaultAsync(ct);
            if (doc is null)
            {
                log.LogError("Failed to create or retrieve cosmetics for account {Account}", accountId);
            }
        }

        var merged = await MergeAsync(mongo, doc is null ? Defaults() : (JsonObject)JsonStringified(doc)!, ct);
        await redis.StringSetAsync(key, Js.Stringify(merged));
        return merged;
    }

    // mergeCosmetics: a taunt entry for every character, its own when it has one, else its first taunt and three blanks.
    private static async Task<JsonObject> MergeAsync(IMongoDatabase mongo, JsonObject cosmetics, CancellationToken ct)
    {
        var assets = await DataAssets.EnabledAsync(mongo, ct);
        var taunts = new JsonObject();
        var own = cosmetics["Taunts"] as JsonObject;
        foreach (var character in assets.Where(a => DataAssets.Str(a, "assetType") == "CharacterData"))
        {
            string slug = DataAssets.Str(character, "slug") ?? "undefined";
            if (own?[slug] is { } entry && Truthy(entry))
            {
                taunts[slug] = entry.DeepClone();
            }
            else
            {
                string first = assets.FirstOrDefault(a => DataAssets.Str(a, "assetType") == "TauntData" && DataAssets.Str(a, "character_slug") == slug) is { } taunt
                    ? DataAssets.Str(taunt, "slug") ?? "" : "";
                taunts[slug] = new JsonObject { ["TauntSlots"] = new JsonArray(first, "", "", "") };
            }
        }

        // { ...cosmetics, Taunts: mergedTaunts }: Taunts keeps its place, or comes last.
        var merged = (JsonObject)cosmetics.DeepClone();
        merged["Taunts"] = taunts;
        return merged;
    }

    // new CosmeticsModel().toObject() without its _id: the schema's defaults, in the schema's order.
    private static JsonObject Defaults() => new()
    {
        ["Taunts"] = s_defaultTaunts.DeepClone(),
        ["AnnouncerPack"] = "announcer_pack_default",
        ["Banner"] = "banner_default",
        ["StatTrackers"] = StatTrackerDefault(),
        ["RingoutVfx"] = "ring_out_vfx_default",
        ["Gems"] = new JsonObject { ["GemSlots"] = new JsonArray("", "", "") },
    };

    // A stored document as JSON.stringify writes the lean read: ObjectIds as hex, dates as ISO text, keys in JS order.
    private static JsonNode? JsonStringified(BsonValue value) => value switch
    {
        BsonNull => null,
        BsonObjectId o => o.Value.ToString(),
        BsonDateTime d => d.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
        BsonDocument doc => new JsonObject(Js.OrderedLikeAnObject(doc, e => e.Name).Select(e => KeyValuePair.Create(e.Name, JsonStringified(e.Value)))),
        BsonArray a => new JsonArray(a.Select(JsonStringified).ToArray()),
        _ => Lean.Value(value),
    };

    private static bool Truthy(JsonNode? node) => node switch
    {
        null => false,
        JsonValue v when v.TryGetValue(out string? s) => s.Length > 0,
        JsonValue v when v.TryGetValue(out bool b) => b,
        JsonValue v when v.TryGetValue(out double d) => d != 0 && !double.IsNaN(d),
        _ => true,
    };

    private static JsonObject Load(string name)
    {
        using var stream = typeof(CosmeticsService).Assembly.GetManifestResourceStream($"OpenVersus.Server.Core.Cosmetics.{name}.json")
            ?? throw new InvalidOperationException($"{name}.json is not embedded");
        return (JsonObject)JsonNode.Parse(stream)!;
    }
}

/// <summary>What the game is answered with: the six fields WB sent, in WB's order (see CosmeticsService).</summary>
public static class CosmeticsAnswer
{
    /// <summary>What the game is answered with, in this order.</summary>
    public static readonly IReadOnlyList<string> AnswerFields = ["Taunts", "Banner", "RingoutVfx", "AnnouncerPack", "StatTrackers", "Gems"];

    /// <summary>The answer: the six fields WB sent, in its order (those the cosmetics have).</summary>
    public static JsonObject Answer(JsonObject cosmetics)
    {
        var equipped = new JsonObject();
        foreach (string field in AnswerFields)
        {
            if (cosmetics.TryGetPropertyValue(field, out var value))
            {
                equipped[field] = value?.DeepClone();
            }
        }

        return new JsonObject { ["body"] = new JsonObject { ["EquippedCosmetics"] = equipped }, ["metadata"] = null, ["return_code"] = 0 };
    }
}

public static class CosmeticsHosting
{
    public static WebApplicationBuilder AddCosmetics(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<ICosmeticsService, CosmeticsService>();
        return builder;
    }
}

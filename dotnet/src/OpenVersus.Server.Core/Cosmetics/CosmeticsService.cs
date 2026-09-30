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

// The writes (PUT /ssc/invoke/equip_taunt, equip_stat_tracker, equip_announcer_pack, equip_banner, equip_ringout_vfx,
// set_profile_icon), ported from handlers/cosmetics.ts and the update* functions in services/cosmeticsService.ts. Each is
// one findAndModify on cosmetics {_id} as mongoose sends it (upsert, new): $setOnInsert {account_id, __v 0, the schema's
// defaults except the field $set touches, in the schema's order}, then $set {path: value} (no $set when the value is
// missing: mongoose drops undefined). Then player:{id}:cosmetics is written, as JSON.stringify writes it:
//   equip_taunt         the equipped cosmetics as read above (the cache when there, with its defaults filled in), the
//                       slot changed, merged; Mongo: $set Taunts.<character>.TauntSlots (Mixed: the value as sent)
//   equip_stat_tracker  the stored document's slots (else ["", "", ""]), the slot changed (cast to text as mongoose's
//                       [String] does); $set StatTrackers.StatTrackerSlots; Redis from the updated document, merged
//   equip_announcer_pack  $set AnnouncerPack; Redis from the updated document, merged
//   equip_banner / equip_ringout_vfx  the equipped cosmetics read first (which may create them), then $set Banner /
//                       RingoutVfx, "default_banner" / "ring_out_vfx_default" for a falsy value; Redis as above
//   set_profile_icon    only an enabled ProfileIconData slug: playertesters updateOne {_id} $set profile_icon, then the
//                       cosmetics upsert with no $set (TS $sets ProfileIcon, which the schema lacks: mongoose drops it),
//                       Redis as above
// A slot index past the end pads with null (a JS array hole); one that is not a non-negative integer changes nothing
// (JS sets a property the array's JSON never shows); an unknown character, a non-ObjectId id, or a value mongoose cannot
// cast fails (TS catches the throw and answers {}).
//
// Unlike there: a cached document without Taunts gets a copy of the default taunts (TS assigns its module-level
// defaultTaunts, which is also the schema default, and the slot write then changes the defaults of every later new
// document in that process); a slot index past MaxSlotIndex fails instead of padding the array that far.

public interface ICosmeticsService
{
    /// <summary>The player's equipped cosmetics as the TS server keeps them (stored fields included).</summary>
    Task<JsonObject> EquippedAsync(string accountId, CancellationToken ct);

    /// <summary>equip_taunt: false when the TS server would fail (and answer {}).</summary>
    Task<bool> EquipTauntAsync(string accountId, JsonNode? character, JsonNode? index, JsonNode? slug, CancellationToken ct);

    /// <summary>equip_stat_tracker: false when the TS server would fail.</summary>
    Task<bool> EquipStatTrackerAsync(string accountId, JsonNode? index, JsonNode? slug, CancellationToken ct);

    /// <summary>equip_announcer_pack, equip_banner, equip_ringout_vfx (<paramref name="given"/>: the body has the
    /// field): false when the TS server would fail.</summary>
    Task<bool> EquipAsync(CosmeticSlot slot, string accountId, JsonNode? slug, bool given, CancellationToken ct);

    /// <summary>set_profile_icon: false for an unknown icon, or when the TS server would fail.</summary>
    Task<bool> SetProfileIconAsync(string accountId, JsonNode? slug, CancellationToken ct);
}

/// <summary>The single-value cosmetics.</summary>
public enum CosmeticSlot
{
    AnnouncerPack,
    Banner,
    RingoutVfx,
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

    /// <summary>The highest slot index written: the game has four taunt slots and three stat trackers, and a larger index
    /// pads the array with nulls up to it (an index of a billion would build a billion-entry array).</summary>
    public const int MaxSlotIndex = 15;

    public async Task<bool> EquipTauntAsync(string accountId, JsonNode? character, JsonNode? index, JsonNode? slug, CancellationToken ct)
    {
        if (!ObjectId.TryParse(accountId, out var id) || Slot(index) is not { } slot)
        {
            return false;
        }

        var cosmetics = await EquippedAsync(accountId, ct);
        // cachedCosmetics.Taunts[character].TauntSlots[index] = value: anything else there throws.
        if (character is not JsonValue name || !name.TryGetValue(out string? characterSlug)
            || cosmetics["Taunts"] is not JsonObject taunts || taunts[characterSlug] is not JsonObject entry
            || entry["TauntSlots"] is not JsonArray slots)
        {
            log.LogError("Error saving taunt for {Account}: no taunts for character {Character}", accountId, character?.ToJsonString());
            return false;
        }

        if (slot >= 0)
        {
            Put(slots, slot, slug?.DeepClone());
        }

        var mongo = Mongo();
        await UpsertAsync(mongo, id, $"Taunts.{characterSlug}.TauntSlots", ToBson(slots), ct);
        await SaveAsync(accountId, await MergeAsync(mongo, cosmetics, ct));
        return true;
    }

    public async Task<bool> EquipStatTrackerAsync(string accountId, JsonNode? index, JsonNode? slug, CancellationToken ct)
    {
        if (!ObjectId.TryParse(accountId, out var id) || Slot(index) is not { } slot || !TryCastString(slug, out var value))
        {
            return false;
        }

        var mongo = Mongo();
        var doc = await mongo.GetCollection<BsonDocument>("cosmetics").Find(new BsonDocument("_id", id)).FirstOrDefaultAsync(ct);
        var slots = doc?.GetValue("StatTrackers", BsonNull.Value) is BsonDocument trackers && trackers.GetValue("StatTrackerSlots", BsonNull.Value) is BsonArray stored
            ? new JsonArray(stored.Select(v => TryCastString(Lean.Value(v), out var s) ? (JsonNode?)s : null).ToArray())
            : new JsonArray("", "", "");
        if (slot >= 0)
        {
            Put(slots, slot, value);
        }

        await SaveAsync(accountId, await MergeAsync(mongo, Stored(await UpsertAsync(mongo, id, "StatTrackers.StatTrackerSlots", ToBson(slots), ct)), ct));
        return true;
    }

    public async Task<bool> EquipAsync(CosmeticSlot slot, string accountId, JsonNode? slug, bool given, CancellationToken ct)
    {
        if (!ObjectId.TryParse(accountId, out var id))
        {
            return false;
        }

        string field = slot.ToString();
        BsonValue? value;
        if (slot == CosmeticSlot.AnnouncerPack)
        {
            // The value as sent; none at all when the body has none.
            if (!TryCastString(slug, out var text))
            {
                return false;
            }

            value = given ? (text is null ? BsonNull.Value : new BsonString(text)) : null;
        }
        else
        {
            // Both read the equipped cosmetics first, which creates them for a player with none.
            await EquippedAsync(accountId, ct);
            string fallback = slot == CosmeticSlot.Banner ? "default_banner" : "ring_out_vfx_default";
            if (!Truthy(slug))
            {
                value = fallback;
            }
            else if (TryCastString(slug, out var text) && text is not null)
            {
                value = text;
            }
            else
            {
                return false;
            }
        }

        var mongo = Mongo();
        await SaveAsync(accountId, await MergeAsync(mongo, Stored(await UpsertAsync(mongo, id, value is null ? null : field, value, ct)), ct));
        return true;
    }

    public async Task<bool> SetProfileIconAsync(string accountId, JsonNode? slug, CancellationToken ct)
    {
        var mongo = Mongo();
        string text = slug is JsonValue v && v.TryGetValue(out string? s) ? s : "";
        var assets = await DataAssets.EnabledAsync(mongo, ct);
        if (!assets.Any(a => DataAssets.Str(a, "assetType") == "ProfileIconData" && DataAssets.Str(a, "slug") == text))
        {
            log.LogWarning("Ignoring unknown profile icon \"{Slug}\" for {Account}", text, accountId);
            return false;
        }

        if (!ObjectId.TryParse(accountId, out var id))
        {
            return false;
        }

        // The player record is what /access, profiles and friends read the icon from.
        var result = await mongo.GetCollection<BsonDocument>("playertesters").UpdateOneAsync(
            new BsonDocument("_id", id), new BsonDocument("$set", new BsonDocument("profile_icon", text)), cancellationToken: ct);
        await SaveAsync(accountId, await MergeAsync(mongo, Stored(await UpsertAsync(mongo, id, null, null, ct)), ct));
        log.LogInformation("Profile icon for {Account} set to {Slug}{Missing}", accountId, text, result.MatchedCount == 0 ? " (no such player)" : "");
        return true;
    }

    // findOneAndUpdate({ _id }, { $set: { [path]: value }, $setOnInsert: { account_id } }, { new: true, upsert: true }) as
    // mongoose sends it: $setOnInsert first, with __v and the defaults of every field the $set does not touch.
    private static async Task<BsonDocument> UpsertAsync(IMongoDatabase mongo, ObjectId id, string? path, BsonValue? value, CancellationToken ct)
    {
        string? field = path?.Split('.')[0];
        var onInsert = new BsonDocument { { "account_id", id }, { "__v", 0 } };
        foreach (var (name, fallback) in Defaults())
        {
            if (name != field)
            {
                onInsert[name] = ToBson(fallback);
            }
        }

        var update = new BsonDocument("$setOnInsert", onInsert);
        if (path is not null)
        {
            update["$set"] = new BsonDocument(path, value ?? BsonNull.Value);
        }

        return await mongo.GetCollection<BsonDocument>("cosmetics").FindOneAndUpdateAsync(
            new BsonDocument("_id", id),
            new BsonDocumentUpdateDefinition<BsonDocument>(update),
            new FindOneAndUpdateOptions<BsonDocument> { IsUpsert = true, ReturnDocument = ReturnDocument.After },
            ct);
    }

    private IMongoDatabase Mongo() => services.GetService<IMongoDatabase>() ?? throw new InvalidOperationException("this service has no Mongo (MONGODB_URI)");

    private async Task SaveAsync(string accountId, JsonObject cosmetics)
    {
        var redis = services.GetService<IConnectionMultiplexer>()?.GetDatabase() ?? throw new InvalidOperationException("this service has no Redis (REDIS)");
        await redis.StringSetAsync($"player:{accountId}:cosmetics", Js.Stringify(cosmetics));
    }

    private static JsonObject Stored(BsonDocument doc) => (JsonObject)JsonStringified(doc)!;

    // A JSON value as the BSON serializer stores it (numbers as BsonDocument.Parse reads them).
    private static BsonValue ToBson(JsonNode? value) => BsonDocument.Parse($"{{\"v\":{Js.Stringify(value)}}}")["v"];

    // arr[index] = value: past the end, the holes read as null.
    private static void Put(JsonArray array, int index, JsonNode? value)
    {
        while (array.Count <= index)
        {
            array.Add(null);
        }

        array[index] = value;
    }

    // A slot index: the element it names (a non-negative integer, or its canonical text), -1 for a key JS would set as a
    // property (no element changes), null past MaxSlotIndex.
    private static int? Slot(JsonNode? index)
    {
        double n = index is JsonValue v
            ? Number(v) is { } d ? d : v.TryGetValue(out string? s) && s.Length > 0 && s.All(char.IsAsciiDigit) && (s.Length == 1 || s[0] != '0') && s.Length <= 10 ? double.Parse(s, CultureInfo.InvariantCulture) : -1
            : -1;
        if (n < 0 || n != Math.Floor(n))
        {
            return -1;
        }

        return n <= MaxSlotIndex ? (int)n : null;
    }

    // A JSON number, whichever CLR type holds it (a parsed body's element, or a value built in code).
    private static double? Number(JsonValue value) =>
        value.GetValueKind() == System.Text.Json.JsonValueKind.Number ? double.Parse(value.ToJsonString(), CultureInfo.InvariantCulture) : null;

    // mongoose's String cast: text as is, null and a missing value as null, numbers and booleans as JS writes them;
    // anything else throws there.
    private static bool TryCastString(JsonNode? value, out string? text)
    {
        text = null;
        switch (value)
        {
            case null:
                return true;
            case JsonValue v when v.TryGetValue(out string? s):
                text = s;
                return true;
            case JsonValue v when v.TryGetValue(out bool b):
                text = b ? "true" : "false";
                return true;
            case JsonValue v when Number(v) is { } d:
                text = Js.Stringify(JsonValue.Create(d));
                return true;
            default:
                return false;
        }
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

using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Access;
using OpenVersus.Server.Core.Matches;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Inventory;

// Taking an asset out of the game for good (ovsctl asset retire): a skin, a profile icon, a banner, a ring-out effect,
// a taunt, a stat tracker, an announcer pack, a perk, an emote... whose asset leaves the paks. The data that names it
// is the players', and none of it follows from a new build:
//
//   playeritems    who was paid it (RewardGrants: items.{slug}) is paid the replacement instead, when one is given:
//                  its count becomes the higher of the two; the retired entry goes either way. A tier that paid it
//                  stays claimed (rewardtracks holds the reward's guid, not the item), so this is how its claimers
//                  come to own what the tier pays now.
//   equipped       wherever a player has it in a slot, the slot holds the fallback (--equip), an enabled asset of the
//                  same kind (and, for a fighter's own skins and taunts, the same fighter):
//                    playertesters  variant (the skin), profile_icon
//                    cosmetics      Banner, RingoutVfx, AnnouncerPack, a fighter's TauntSlots, StatTrackerSlots, GemSlots
//                    perkpages      a page's Perks
//                  each slot by its own update, guarded by the value it had: a player equipping something else at
//                  that moment keeps what they chose.
//   dataassets     its catalog row is deleted, and the config CRC bumped as an asset sync bumps it: the inventory and
//                  the hiss list every enabled row, and every game service's hiss is built again for the new number.
//   Redis          for the players it changed and the players online (online_players): the lobby pick and the session
//                  (player:{id} and connections:{id} skin and profileIcon, with the session's IP copy) hold the
//                  fallback; the cached cosmetics (player:{id}:cosmetics, read before Mongo) are deleted, to be read
//                  from Mongo again; the match's copy (connections:{id}:cosmetics) is rewritten. A lobby's own member
//                  documents are left: they are written again from the pick at the next loadout lock or lobby.
//
// A dry run unless applied: the counts of what would change, nothing written. Applying is refused while a player has
// it equipped and no fallback is named. Run again, it changes only what came back since (a game still running the old
// paks can equip it again until it updates): every step finds what names the asset now.
// A fighter (CharacterData) is refused: a fighter is its skins, taunts, perk pages, ratings and stats too, not a slot's
// value.
// Last, read only: every collection is searched for the asset's name (as text, in each document), but the match
// archives and any collection too large to read through, so what is left is seen, not assumed: a match's history may
// keep naming what it was played with, and nothing here rewrites that.

/// <summary>A collection and how many of its documents name the asset.</summary>
public sealed record AssetMention(string Collection, long Documents);

/// <summary>
/// An asset's retirement, done or dry-run: its kind (null when the catalog no longer holds it), who was (or would be)
/// paid the replacement, the players who had it equipped by collection, the players whose Redis state held it, its
/// catalog rows; whether the replacement is in the catalog and owned only once paid; and where the asset is still
/// named afterwards (in a dry run: now), with the collections that were not searched.
/// </summary>
public sealed record AssetRetirementView(bool Applied, string Asset, string? AssetType, string? ReplaceWith, string? Equip, long Owners, IReadOnlyList<AssetMention> Equipped,
    long SessionsChanged, long AssetRowsRemoved, bool ReplacementInCatalog, bool ReplacementPaidOnly, IReadOnlyList<AssetMention> StillNamedIn, IReadOnlyList<string> NotSearched);

public static class AssetRetirement
{
    /// <summary>A collection with more documents than this is not searched for the asset's name (it is listed instead).</summary>
    public const long SearchLimit = 500_000;

    // Where a player has an asset equipped: the collection, the top-level fields its slots are under (null: all but the
    // ids), and the field holding the player's id.
    private static readonly (string Collection, string[]? Fields, string IdField)[] s_equipped =
    [
        (PlayerRecord.Collection, ["variant", "profile_icon"], "_id"),
        ("cosmetics", null, "_id"),
        ("perkpages", ["perk_pages"], "account_id"),
    ];

    /// <summary>The view, or why it was refused (nothing written).</summary>
    public static async Task<(AssetRetirementView? View, string? Refused)> RunAsync(IMongoDatabase mongo, IDatabase? redis, string asset, string? replaceWith, string? equip,
        bool apply, ILogger log, CancellationToken ct)
    {
        asset = asset.Trim();
        replaceWith = replaceWith?.Trim() is { Length: > 0 } r ? r : null;
        equip = equip?.Trim() is { Length: > 0 } e ? e : null;
        foreach (var (name, slug) in new[] { ("asset", asset), ("replacement", replaceWith), ("fallback", equip) })
        {
            if (slug is not null && (slug.Length == 0 || slug.Contains('.') || slug.StartsWith('$')))
            {
                return (null, $"the {name} is not an asset's slug: \"{slug}\"");
            }
        }

        if (asset == replaceWith || asset == equip)
        {
            return (null, "the asset cannot be its own replacement or fallback");
        }

        var assets = mongo.GetCollection<BsonDocument>("dataassets");
        var rows = await assets.Find(new BsonDocument("slug", new BsonDocument("$in", new BsonArray(new[] { asset, replaceWith, equip }.OfType<string>())))).ToListAsync(ct);
        var own = rows.Where(row => Str(row, "slug") == asset).ToList();
        string? type = own.Select(row => Str(row, "assetType")).FirstOrDefault(t => t is not null);
        if (own.Any(row => Str(row, "assetType") == "CharacterData"))
        {
            return (null, $"{asset} is a fighter: a fighter is its skins, taunts, perk pages, ratings and stats too, which this does not retire");
        }

        if (equip is not null)
        {
            if (rows.FirstOrDefault(row => Str(row, "slug") == equip && Enabled(row)) is not { } fallback)
            {
                return (null, $"the fallback {equip} is not an enabled asset in the catalog (dataassets)");
            }

            if (own.FirstOrDefault(row => Str(row, "assetType") != Str(fallback, "assetType")) is { } otherKind)
            {
                return (null, $"the fallback {equip} is a {Str(fallback, "assetType")}, {asset} is a {Str(otherKind, "assetType")}");
            }

            if (own.FirstOrDefault(row => !string.Equals(Str(row, "character_slug") ?? "", Str(fallback, "character_slug") ?? "", StringComparison.OrdinalIgnoreCase)) is { } otherFighter)
            {
                return (null, $"the fallback {equip} is {Owner(fallback)}, {asset} is {Owner(otherFighter)}");
            }
        }

        bool inCatalog = replaceWith is not null && rows.Any(row => Str(row, "slug") == replaceWith && Enabled(row));
        if (apply && replaceWith is not null && !inCatalog)
        {
            return (null, $"the replacement {replaceWith} is not an enabled asset in the catalog (dataassets): sync its row first, or its new owners hold an item the game is not told of");
        }

        // Who has it equipped, slot by slot, and whose Redis state holds it: read first, so nothing is written when
        // the fallback it needs is missing.
        var slots = new List<(string Collection, BsonValue Id, string Account, List<string> Paths)>();
        foreach (var (collection, fields, idField) in s_equipped)
        {
            using var cursor = await mongo.GetCollection<BsonDocument>(collection).Find(FilterDefinition<BsonDocument>.Empty).ToCursorAsync(ct);
            while (await cursor.MoveNextAsync(ct))
            {
                foreach (var doc in cursor.Current)
                {
                    var paths = new List<string>();
                    foreach (var field in doc.Where(f => fields?.Contains(f.Name) ?? f.Name is not ("_id" or "account_id")))
                    {
                        Slots(field.Value, field.Name, asset, paths);
                    }

                    if (paths.Count > 0)
                    {
                        slots.Add((collection, doc["_id"], doc.GetValue(idField, doc["_id"]).ToString() ?? "", paths));
                    }
                }
            }
        }

        var accounts = slots.Select(s => s.Account).Where(a => a.Length > 0).ToHashSet();
        if (redis is not null)
        {
            accounts.UnionWith((await redis.SetMembersAsync("online_players")).Select(m => m.ToString()));
        }

        var holding = new List<string>();
        foreach (string id in accounts.Order(StringComparer.Ordinal))
        {
            if (redis is not null && await SessionAsync(redis, id, asset, equip: null))
            {
                holding.Add(id);
            }
        }

        if (apply && equip is null && (slots.Count > 0 || holding.Count > 0))
        {
            return (null, $"{slots.Select(s => s.Account).Distinct().Count()} player(s) have {asset} equipped ({holding.Count} in Redis): name what they hold instead with --equip");
        }

        var items = mongo.GetCollection<BsonDocument>(RewardTracks.RewardGrants.Collection);
        var owned = new BsonDocument($"items.{asset}", new BsonDocument("$exists", true));
        var row = new BsonDocument("slug", asset);
        long owners = await items.CountDocumentsAsync(owned, cancellationToken: ct);
        long assetRows = await assets.CountDocumentsAsync(row, cancellationToken: ct);
        if (apply)
        {
            // The replacement's count: the higher of what the player holds of each, in one update per player.
            var move = new List<BsonDocument>();
            if (replaceWith is not null)
            {
                move.Add(new BsonDocument("$set", new BsonDocument($"items.{replaceWith}", new BsonDocument("$max", new BsonArray { $"$items.{replaceWith}", $"$items.{asset}" }))));
            }

            move.Add(new BsonDocument("$unset", $"items.{asset}"));
            owners = (await items.UpdateManyAsync(owned, (PipelineDefinition<BsonDocument, BsonDocument>)move.ToArray(), cancellationToken: ct)).ModifiedCount;
            foreach (var (collection, id, _, paths) in slots)
            {
                foreach (string path in paths)
                {
                    await mongo.GetCollection<BsonDocument>(collection).UpdateOneAsync(new BsonDocument { { "_id", id }, { path, asset } },
                        new BsonDocument("$set", new BsonDocument(path, equip)), cancellationToken: ct);
                }
            }

            assetRows = (await assets.DeleteManyAsync(row, ct)).DeletedCount;
            if (assetRows > 0)
            {
                // As POST /syncAsset's UpdateCrc: the catalog changed.
                await mongo.GetCollection<BsonDocument>("config").UpdateOneAsync(FilterDefinition<BsonDocument>.Empty,
                    new BsonDocument("$inc", new BsonDocument("CRC", 1)), new UpdateOptions { IsUpsert = true }, ct);
            }

            foreach (string id in holding)
            {
                await SessionAsync(redis!, id, asset, equip);
            }
        }

        var equipped = slots.GroupBy(s => s.Collection).Select(g => new AssetMention(g.Key, g.Count())).OrderBy(m => m.Collection, StringComparer.Ordinal).ToList();
        var (named, notSearched) = await NamedInAsync(mongo, asset, ct);
        var view = new AssetRetirementView(apply, asset, type, replaceWith, equip, owners, equipped, holding.Count, assetRows, inCatalog,
            replaceWith is not null && Ownership.IsRestricted(replaceWith), named, notSearched);
        if (apply)
        {
            log.LogWarning("Asset {Asset} ({Type}) retired: {Owners} owner(s){Replaced}; equipped by {Equipped}, now {Equip}; {Sessions} player(s) in Redis; {Rows} catalog row(s) removed; still named in {Named}",
                asset, type ?? "not in the catalog", owners, replaceWith is null ? "" : $" now hold {replaceWith}",
                equipped.Count == 0 ? "nobody" : string.Join(", ", equipped.Select(m => $"{m.Collection} ({m.Documents})")), equip ?? "nothing",
                holding.Count, assetRows, named.Count == 0 ? "nothing searched" : string.Join(", ", named.Select(n => $"{n.Collection} ({n.Documents})")));
        }

        return (view, null);
    }

    // The paths (a.b.2: a field, or an array's index) under which value is exactly the asset's slug.
    private static void Slots(BsonValue value, string path, string asset, List<string> found)
    {
        switch (value)
        {
            case BsonString text when text.Value == asset:
                found.Add(path);
                break;
            case BsonDocument doc:
                // A field no update path can name is left (none of these collections has one).
                foreach (var field in doc.Where(f => f.Name.Length > 0 && !f.Name.Contains('.') && !f.Name.StartsWith('$')))
                {
                    Slots(field.Value, $"{path}.{field.Name}", asset, found);
                }

                break;
            case BsonArray array:
                for (int i = 0; i < array.Count; i++)
                {
                    Slots(array[i], $"{path}.{i}", asset, found);
                }

                break;
        }
    }

    // Whether a player's Redis state holds the asset: the lobby pick and the session (skin, profileIcon), the cached
    // cosmetics and the match's copy of them. With a fallback, they hold it instead (the cache is deleted: it is read
    // from Mongo again).
    private static async Task<bool> SessionAsync(IDatabase redis, string id, string asset, string? equip)
    {
        bool held = false;
        string quoted = $"\"{asset}\"";
        foreach (string key in new[] { $"player:{id}", $"connections:{id}" })
        {
            foreach (string field in new[] { "skin", "profileIcon" })
            {
                if ((string?)await redis.HashGetAsync(key, field) != asset)
                {
                    continue;
                }

                held = true;
                if (equip is null)
                {
                    continue;
                }

                await redis.HashSetAsync(key, field, equip);
                // The session's IP copy, while it is this player's (as PlayedLoadout keeps it).
                if (key.StartsWith("connections:", StringComparison.Ordinal) && (string?)await redis.HashGetAsync(key, "current_ip") is { Length: > 0 } ip
                    && (string?)await redis.HashGetAsync($"connections:{ip}", "id") == id)
                {
                    await redis.HashSetAsync($"connections:{ip}", field, equip);
                }
            }
        }

        if (((string?)await redis.StringGetAsync($"player:{id}:cosmetics"))?.Contains(quoted, StringComparison.Ordinal) == true)
        {
            held = true;
            if (equip is not null)
            {
                await redis.KeyDeleteAsync($"player:{id}:cosmetics");
            }
        }

        foreach (var field in await redis.HashGetAllAsync($"connections:{id}:cosmetics"))
        {
            if (!field.Value.ToString().Contains(quoted, StringComparison.Ordinal))
            {
                continue;
            }

            held = true;
            if (equip is not null)
            {
                // Each field is JSON, and a slug is plain text: its quoted form is the value and nothing else.
                await redis.HashSetAsync($"connections:{id}:cosmetics", field.Name, field.Value.ToString().Replace(quoted, $"\"{equip}\"", StringComparison.Ordinal));
            }
        }

        return held;
    }

    // Read only: each collection's documents that name the asset anywhere (as text), the largest collections aside.
    private static async Task<(List<AssetMention> Named, List<string> NotSearched)> NamedInAsync(IMongoDatabase mongo, string asset, CancellationToken ct)
    {
        var named = new List<AssetMention>();
        var notSearched = new List<string>();
        foreach (string name in (await (await mongo.ListCollectionNamesAsync(cancellationToken: ct)).ToListAsync(ct)).Order(StringComparer.Ordinal))
        {
            if (name.StartsWith("system.", StringComparison.Ordinal))
            {
                continue;
            }

            var collection = mongo.GetCollection<BsonDocument>(name);
            if (name == GameStats.ArchiveCollection || await collection.EstimatedDocumentCountAsync(cancellationToken: ct) > SearchLimit)
            {
                notSearched.Add(name);
                continue;
            }

            long documents = 0;
            using var cursor = await collection.Find(FilterDefinition<BsonDocument>.Empty).ToCursorAsync(ct);
            while (await cursor.MoveNextAsync(ct))
            {
                documents += cursor.Current.Count(d => d.ToJson().Contains(asset, StringComparison.Ordinal));
            }

            if (documents > 0)
            {
                named.Add(new AssetMention(name, documents));
            }
        }

        return (named, notSearched);
    }

    private static string Owner(BsonDocument row) => Str(row, "character_slug") is { Length: > 0 } fighter ? $"{fighter}'s" : "no fighter's";

    private static string? Str(BsonDocument row, string field) => Assets.DataAssets.Str(row, field);

    private static bool Enabled(BsonDocument row) => row.GetValue("enabled", BsonNull.Value) is BsonBoolean { Value: true };
}

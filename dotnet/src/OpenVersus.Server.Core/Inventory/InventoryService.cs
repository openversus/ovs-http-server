using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Access;

namespace OpenVersus.Server.Core.Inventory;

// The player's inventory, ported from the TS server's handleProfiles_id_inventory (handlers/profiles.ts) with
// unlockAll (data/characters.ts), getToastInventoryEntry (data/toast.ts) and loadAssets (loadAssets.ts), branch
// infinity-war. Every player owns everything: one item per enabled data asset, then one per perk (with every
// character), one per taunt in the TS taunt list, then Gleamium (9001 for every player, on purpose) and the player's
// toasts. This is the ownership the
// store layouts will be computed from (docs/FROZEN-ACCOUNT-DATA.md).
//
// Mongo, read     dataassets (enabled), in the order Mongo returns them, test characters' assets left out
// Mongo, written  playercounters: the player's document, created with 100 toasts on first use, updatedAt set on
//                 every call (see DailyToastBonus.GetCountersAsync)
//
// Differences from the TS server: the assets are read per request, not from the TS server's startup cache; a request
// whose player is not known (no id) gets 100 toasts without a counters document being written for the empty id; with
// no Mongo the answer is 503 (the TS request never gets one).

public interface IInventoryService
{
    /// <summary>The inventory of <paramref name="accountId"/> (the resolved player, or the token's id); null when this service has no Mongo.</summary>
    Task<JsonArray?> InventoryAsync(string accountId, CancellationToken ct = default);
}

internal sealed class InventoryService(IServiceProvider services, TimeProvider time, ILogger<InventoryService> log) : IInventoryService
{
    // TS data/testCharacters.ts; ENABLE_TEST_CHARACTERS (off on every server) is not ported.
    private static readonly HashSet<string> s_testCharacters = new(
        ["character_supershaggy", "character_Meeseeks", "character_C022", "character_C033", "character_cmanny", "character_manny", "character_C037", "character_C099"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly Lazy<string> s_gleamium = new(() => Template("inventory-gleamium"));
    private static readonly Lazy<string> s_toast = new(() => Template("inventory-toast"));
    private static readonly Lazy<string[]> s_taunts = new(() =>
        JsonNode.Parse(Template("inventory-taunts"))!.AsArray().SelectMany(c => c!["taunt_slugs"]!.AsArray().Select(t => (string)t!)).ToArray());

    public async Task<JsonArray?> InventoryAsync(string accountId, CancellationToken ct)
    {
        if (services.GetService<IMongoDatabase>() is not { } mongo)
        {
            log.LogError("Refusing an inventory for {Account}: this service has no Mongo (MONGODB_URI)", accountId);
            return null;
        }

        var assets = (await mongo.GetCollection<BsonDocument>("dataassets").Find(new BsonDocument("enabled", true)).ToListAsync(ct))
            .Where(a => !(Str(a, "assetType") == "CharacterData" && IsTestCharacter(Str(a, "slug"))) && !IsTestCharacter(Str(a, "character_slug")))
            .ToList();

        var items = new JsonArray();
        foreach (var asset in assets)
        {
            items.Add(Item(accountId, asset.GetValue("slug", BsonNull.Value), serverData: null, accountFirst: false));
        }

        var characters = new JsonObject();
        foreach (var character in assets.Where(a => Str(a, "assetType") == "CharacterData"))
        {
            characters[Str(character, "slug") ?? "undefined"] = true;
        }

        foreach (var perk in assets.Where(a => Str(a, "assetType") == "MvsPerkHsda"))
        {
            items.Add(Item(accountId, perk.GetValue("slug", BsonNull.Value), new JsonObject { ["characters"] = characters.DeepClone() }, accountFirst: true));
        }

        foreach (string taunt in s_taunts.Value)
        {
            items.Add(Item(accountId, taunt, serverData: null, accountFirst: true));
        }

        items.Add(JsonNode.Parse(s_gleamium.Value));
        items.Add(await ToastAsync(mongo, accountId, ct));
        return items;
    }

    // {...ToastData, count: counters.match_toasts, updated_at: now}: the two fields keep their places.
    private async Task<JsonNode> ToastAsync(IMongoDatabase mongo, string accountId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        long count = DailyToastBonus.StartingToasts;
        if (accountId.Length > 0)
        {
            var counters = await DailyToastBonus.GetCountersAsync(mongo, accountId, now, ct);
            count = counters.GetValue("match_toasts", BsonNull.Value) is { IsNumeric: true } toasts ? toasts.ToInt64() : count;
        }
        else
        {
            log.LogWarning("Inventory for a request with no player: 100 toasts, no counters written");
        }

        var toast = JsonNode.Parse(s_toast.Value)!.AsObject();
        toast["count"] = count;
        toast["updated_at"] = new JsonObject { ["_hydra_unix_date"] = now.ToUnixTimeSeconds() };
        return toast;
    }

    /// <summary>
    /// One unlockAll item, with a new id each time as there. Assets write item_slug before account_id; perks and taunts
    /// the other way round.
    /// </summary>
    private static JsonObject Item(string accountId, BsonValue slug, JsonObject? serverData, bool accountFirst)
    {
        var item = new JsonObject
        {
            ["id"] = ObjectId.GenerateNewId().ToString(),
            ["count"] = 1,
            ["data"] = new JsonObject(),
            ["actions"] = new JsonArray(),
            ["server_data"] = serverData ?? new JsonObject(),
        };
        // A missing slug is undefined there, which JSON leaves out.
        JsonNode? itemSlug = slug.IsString ? slug.AsString : null;
        if (accountFirst)
        {
            item["account_id"] = accountId;
        }

        if (itemSlug is not null || !slug.IsBsonNull)
        {
            item["item_slug"] = itemSlug;
        }

        if (!accountFirst)
        {
            item["account_id"] = accountId;
        }

        item["currency_data"] = new JsonObject
        {
            ["source_slug"] = null, ["total_spent"] = null, ["total_earned"] = null, ["total_refunded"] = 0,
            ["should_expire"] = null, ["expires_at"] = null, ["purchase_id"] = null, ["source_platform"] = null,
        };
        item["updated_at"] = new JsonObject { ["_hydra_unix_date"] = 1719953609 };
        item["created_at"] = new JsonObject { ["_hydra_unix_date"] = 1719953609 };
        item["result_type"] = "simple";
        return item;
    }

    private static string? Str(BsonDocument doc, string field) => doc.GetValue(field, BsonNull.Value) is { IsString: true } v ? v.AsString : null;

    private static bool IsTestCharacter(string? slug) => !string.IsNullOrEmpty(slug) && s_testCharacters.Contains(slug);

    private static string Template(string name)
    {
        using var stream = typeof(InventoryService).Assembly.GetManifestResourceStream($"OpenVersus.Server.Core.Inventory.{name}.json")
            ?? throw new InvalidOperationException($"Inventory/{name}.json is not embedded");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

public static class InventoryHosting
{
    public static WebApplicationBuilder AddInventory(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<IInventoryService, InventoryService>();
        return builder;
    }
}

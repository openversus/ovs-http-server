using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Assets;
using OpenVersus.Server.Core.Access;
using OpenVersus.Server.Core.Settings;

namespace OpenVersus.Server.Core.Inventory;

// The player's inventory, ported from the TS server's handleProfiles_id_inventory (handlers/profiles.ts) with
// unlockAll (data/characters.ts), getToastInventoryEntry (data/toast.ts) and loadAssets (loadAssets.ts), branch
// infinity-war. Every player owns everything (but End Game's restricted items, Ownership.cs): one item per enabled data asset, then one per perk (with every
// character), one per taunt in the TS taunt list, then Gleamium (9001 for every player, on purpose) and the player's
// toasts. This is the ownership the
// store layouts will be computed from (docs/FROZEN-ACCOUNT-DATA.md).
//
// Mongo, read     dataassets (enabled), in the order Mongo returns them, test characters' assets left out
// Mongo, written  playercounters: the player's document, created with 100 toasts on first use, updatedAt set on
//                 every call (see DailyToastBonus.GetCountersAsync)
// Mongo, read     playeritems (what rewards paid: AddGrantedAsync)
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

        var assets = await DataAssets.EnabledAsync(mongo, ct);

        var items = new JsonArray();
        var ownership = services.GetService<IOptionsMonitor<OwnershipSettings>>()?.CurrentValue ?? new OwnershipSettings();
        foreach (var asset in assets)
        {
            // End Game's battle pass and Fighter Pass rewards come from what rewards paid (below); the OVS Dev badge is
            // the dev accounts' (Ownership).
            string? slug = Str(asset, "slug");
            if (Ownership.IsRestricted(slug) && !(Ownership.IsDevBadge(slug) && Ownership.IsDevAccount(ownership, accountId)))
            {
                continue;
            }

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

        foreach (string taunt in s_taunts.Value.Where(t => !Ownership.IsRestricted(t)))
        {
            items.Add(Item(accountId, taunt, serverData: null, accountFirst: true));
        }

        items.Add(JsonNode.Parse(s_gleamium.Value));
        items.Add(await ToastAsync(mongo, accountId, ct));
        await AddGrantedAsync(items, accountId, ct);
        return items;
    }

    // What rewards paid the player (RewardTracks.RewardGrants) that the list above does not hold: a currency as the toast
    // entry is written (WB's inventory had perk_currency so), anything else as an unlockAll item with its count. Items the
    // list holds keep their entries as they are (every player owns them already).
    private async Task AddGrantedAsync(JsonArray items, string accountId, CancellationToken ct)
    {
        if (services.GetService<RewardTracks.IRewardGrants>() is not { } grants)
        {
            return;
        }

        var listed = items.OfType<JsonObject>().Select(i => i["item_slug"]?.GetValue<string>()).OfType<string>().ToHashSet();
        foreach (var (slug, count) in await grants.ItemsAsync(accountId, ct))
        {
            if (listed.Contains(slug) || count <= 0)
            {
                continue;
            }

            if (RewardTracks.RewardData.Currencies.Contains(slug))
            {
                var currency = JsonNode.Parse(s_toast.Value)!.AsObject();
                currency["item_slug"] = slug;
                currency["count"] = count;
                currency["currency_sources"] = new JsonArray(new JsonObject
                {
                    ["source_slug"] = null, ["total_spent"] = 0, ["total_earned"] = count, ["total_refunded"] = 0,
                    ["should_expire"] = false, ["expires_at"] = null, ["purchase_id"] = null, ["source_platform"] = null,
                });
                currency["updated_at"] = new JsonObject { ["_hydra_unix_date"] = time.GetUtcNow().ToUnixTimeSeconds() };
                items.Add(currency);
            }
            else
            {
                var item = Item(accountId, slug, serverData: null, accountFirst: false);
                item["count"] = count;
                items.Add(item);
            }
        }
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
        builder.AddSetting<OwnershipSettings>("Ownership");
        builder.Services.AddSingleton<IInventoryService, InventoryService>();
        return builder;
    }
}

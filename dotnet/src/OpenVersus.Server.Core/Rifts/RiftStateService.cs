using System.ComponentModel;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Compat;

namespace OpenVersus.Server.Core.Rifts;

// GET /ssc/invoke/get_or_create_rift_state: the player's rift state (the client's FRiftState: what was played last, the
// daily reward rifts, the attrition pools), which the rift select page waits for. Neither the TS server nor any capture
// answers it; the answer is {body: {RiftState: <state>}} (the game's handler reads RiftState: see Answer), the state's
// shape the one the game cached from WB's server (SaveGames HydraRiftStateJson.sav holds the state), and a new player gets what that cache shows for an account's untouched pools: only the global pool,
// full. The per-rift pools (rifts whose config has Attrition.bTargetRiftPoolInsteadOfGlobalPool) appear once a rift
// uses them.
//
// Mongo, read     riftstates {account_id}
// Mongo, written  riftstates insert for a player with none: account_id, state (the answer's RiftState), createdAt, updatedAt;
//                 $set state, updatedAt when a rift match changes it (RiftProgressService)

/// <summary>Rift settings.</summary>
public sealed class RiftSettings
{
    [Description("Stocks in the global attrition pool of a new player's rift state.")]
    public int GlobalAttritionStocks { get; set; } = 6;

    [Description("Daily attempts in the global attrition pool of a new player's rift state.")]
    public int GlobalDailyAttempts { get; set; } = 5;

    [Description("Years added to every rift's end time, so none ends (0: the dates as they are). Read at startup.")]
    public int EndTimeYears { get; set; } = 20;

    [Description("Offer the rifts the hiss has and the TS load_rifts answer lacks (the Season 6 rogue rifts). Read at startup.")]
    public bool OfferHissOnlyRifts { get; set; } = true;
}

public interface IRiftStateService
{
    /// <summary>The rift state answer for the player the session token (<paramref name="claims"/>) names; created on first use.</summary>
    Task<JsonNode> GetOrCreateAsync(JsonObject? claims, CancellationToken ct);

    /// <summary>The rift state itself (the answer's RiftState) of <paramref name="playerId"/>; created on first use.</summary>
    Task<JsonObject> StateAsync(string playerId, CancellationToken ct);

    /// <summary>Replaces the stored rift state of <paramref name="playerId"/>.</summary>
    Task SaveAsync(string playerId, JsonObject state, CancellationToken ct);
}

internal sealed class RiftStateService(IServiceProvider services, IOptionsMonitor<RiftSettings> settings, TimeProvider time, ILogger<RiftStateService> log) : IRiftStateService
{
    public const string Collection = "riftstates";

    public async Task<JsonNode> GetOrCreateAsync(JsonObject? claims, CancellationToken ct)
    {
        string? playerId = claims?["id"] is JsonValue v && v.TryGetValue(out string? id) && id.Length > 0 ? id : null;
        if (playerId is null)
        {
            return Answer(NewState(settings.CurrentValue));
        }

        return Answer(await StateAsync(playerId, ct));
    }

    public async Task<JsonObject> StateAsync(string playerId, CancellationToken ct)
    {
        var mongo = services.GetService<IMongoDatabase>() ?? throw new InvalidOperationException("this service has no Mongo (MONGODB_URI)");
        var states = mongo.GetCollection<BsonDocument>(Collection);
        var stored = await states.Find(new BsonDocument("account_id", playerId)).FirstOrDefaultAsync(ct);
        if (stored?["state"] is BsonDocument state)
        {
            var read = Lean.Value(state)!.AsObject();
            AddRiftPools(read);
            return read;
        }

        var created = NewState(settings.CurrentValue);
        AddRiftPools(created);
        var now = time.GetUtcNow().UtcDateTime;
        await states.InsertOneAsync(new BsonDocument
        {
            ["account_id"] = playerId,
            ["state"] = BsonDocument.Parse(Js.Stringify(created)),
            ["createdAt"] = now,
            ["updatedAt"] = now,
        }, cancellationToken: ct);
        log.LogInformation("Created the rift state of {Player}", playerId);
        return created;
    }

    public async Task SaveAsync(string playerId, JsonObject state, CancellationToken ct)
    {
        var mongo = services.GetService<IMongoDatabase>() ?? throw new InvalidOperationException("this service has no Mongo (MONGODB_URI)");
        await mongo.GetCollection<BsonDocument>(Collection).UpdateOneAsync(new BsonDocument("account_id", playerId),
            new BsonDocument("$set", new BsonDocument
            {
                ["state"] = BsonDocument.Parse(Js.Stringify(state)),
                ["updatedAt"] = time.GetUtcNow().UtcDateTime,
            }), cancellationToken: ct);
    }

    /// <summary>
    /// Adds a full pool to PlayerAttrition for each offered rift that keeps its own (RiftData.Attrition
    /// bTargetRiftPoolInsteadOfGlobalPool: the tutorial, Triple Threat and the rogue rifts), where the state has none: its
    /// InitialStocks, in the shape WB's server wrote for that kind (the WB-era cache: a rogue rift's with daily attempts
    /// and PremiumResetAttempts, the others' with CurrentResetAttempts). WB's state had pools only for rifts played;
    /// here every one exists from the start, a guess at why the Season 5 page (all rogue rifts) waited forever with no
    /// cached state on the client.
    /// </summary>
    internal static void AddRiftPools(JsonObject state)
    {
        if (state["PlayerAttrition"] is not JsonObject pools)
        {
            state["PlayerAttrition"] = pools = new JsonObject();
        }

        foreach (var config in RiftCatalog.Configs.OfType<JsonObject>())
        {
            string? slug = config["slug"] is JsonValue v && v.TryGetValue(out string? s) ? s : null;
            if (slug is null || pools.ContainsKey(slug) || config["RiftData"]?["Attrition"] is not JsonObject attrition
                || attrition["bTargetRiftPoolInsteadOfGlobalPool"] is not JsonValue own || !own.TryGetValue(out bool isOwn) || !isOwn)
            {
                continue;
            }

            int stocks = (int)(RiftMissions.Number(attrition["InitialStocks"]) ?? 0);
            bool rogue = RiftMissions.Number(config["RiftType"]) == 4;
            pools[slug] = rogue
                ? new JsonObject
                {
                    ["CurrentAttritionDamage"] = 0,
                    ["CurrentAttritionRegenTimestamp"] = 0,
                    ["CurrentAttritionStocks"] = stocks,
                    ["CurrentDailyAttempts"] = 5,
                    ["PremiumResetAttempts"] = 0,
                }
                : new JsonObject
                {
                    ["CurrentAttritionDamage"] = 0,
                    ["CurrentAttritionRegenTimestamp"] = 0,
                    ["CurrentAttritionStocks"] = stocks,
                    ["CurrentResetAttempts"] = 0,
                };
        }
    }

    /// <summary>A new player's rift state: nothing played, no daily rewards, the global attrition pool full.</summary>
    internal static JsonObject NewState(RiftSettings settings) => new()
    {
        ["DailyRewards"] = new JsonObject
        {
            ["Chapter"] = "",
            ["Chapters"] = new JsonArray(),
            ["CompletedNodes"] = new JsonObject(),
            ["RiftSlug"] = "",
            ["Rifts"] = new JsonArray(),
        },
        ["LastPlayed"] = new JsonObject
        {
            ["Chapter"] = "",
            ["NodeId"] = "",
            ["RiftSlug"] = "",
        },
        ["PlayerAttrition"] = new JsonObject
        {
            ["GlobalAttrition"] = new JsonObject
            {
                ["CurrentAttritionDamage"] = 0,
                ["CurrentAttritionRegenTimestamp"] = 0,
                ["CurrentAttritionStocks"] = settings.GlobalAttritionStocks,
                ["CurrentDailyAttempts"] = settings.GlobalDailyAttempts,
                ["CurrentResetAttempts"] = 0,
            },
        },
    };

    // The game's handler for this answer (0x142a2dcc0, build f97148ff; the callback of the request built at 0x142a2f3e0)
    // reads body["RiftState"] and fails without it: the page then waits forever. The state is not the body itself, as
    // the game's save cache (which holds only the state) had suggested.
    internal static JsonObject Answer(JsonObject state) => new()
    {
        ["body"] = new JsonObject { ["RiftState"] = state },
        ["metadata"] = null,
        ["return_code"] = 0,
    };
}

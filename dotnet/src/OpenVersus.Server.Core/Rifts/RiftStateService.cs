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
// answers it; the shape is the one the game cached from WB's server (SaveGames HydraRiftStateJson.sav, which holds this
// answer's body), and a new player gets what that cache shows for an account's untouched pools: only the global pool,
// full. The per-rift pools (rifts whose config has Attrition.bTargetRiftPoolInsteadOfGlobalPool) appear once a rift
// uses them.
//
// Mongo, read     riftstates {account_id}
// Mongo, written  riftstates insert for a player with none: account_id, state (the answer's body), createdAt, updatedAt

/// <summary>Rift settings.</summary>
public sealed class RiftSettings
{
    [Description("Stocks in the global attrition pool of a new player's rift state.")]
    public int GlobalAttritionStocks { get; set; } = 6;

    [Description("Daily attempts in the global attrition pool of a new player's rift state.")]
    public int GlobalDailyAttempts { get; set; } = 5;
}

public interface IRiftStateService
{
    /// <summary>The rift state answer for the player the session token (<paramref name="claims"/>) names; created on first use.</summary>
    Task<JsonNode> GetOrCreateAsync(JsonObject? claims, CancellationToken ct);

    /// <summary>The rift state itself (the answer's body) of <paramref name="playerId"/>; created on first use.</summary>
    Task<JsonObject> StateAsync(string playerId, CancellationToken ct);
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
            return Lean.Value(state)!.AsObject();
        }

        var created = NewState(settings.CurrentValue);
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

    private static JsonObject Answer(JsonObject state) => new()
    {
        ["body"] = state,
        ["metadata"] = null,
        ["return_code"] = 0,
    };
}

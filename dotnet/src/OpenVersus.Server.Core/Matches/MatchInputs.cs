using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Settings;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Matches;

// POST /ovs_match_inputs, C# only (no TS route): a rollback server's recording of every player's input for every frame
// of its match (the rollback server's InputRecording settings), sent once when the match ends, or when the server shuts
// down with the match still running (endedBy "Shutdown": a partial recording). Stored as one document per player per
// match, for replays and player doppelgangers.
//
// Request         header MatchUpdateKey (Rollback:MatchUpdateKey, compared as the TS server compares it: ignoring case,
//                 in constant time); body {matchId, key, endedBy, startedAtUtc, endedAtUtc, frameRate, durationFrames,
//                 droppedInputs, players: [{playerIndex, playerId, playerName, playerCharacter, frames, receivedFrames,
//                 encoding, inputs (base64), missing: [[from, to]]}]}
// Answers         {status: "ok"}; 403 {error: "Invalid signature"} (key missing or wrong, or none configured), 400 {error}
//                 (no body, no matchId, no players, or the match's own key differs), 500 {error} (not stored). The
//                 rollback server logs the status and nothing else.
// Redis, read     {match} (the match notification, while it lasts: 20 min; its matchKey must equal key when it is
//                 there; map, mode, isCustomGame, each player's teamIndex), match:{match} (matchType),
//                 connections:{player} GameplayPreferences
// Mongo, read     playertesters {_id} GameplayPreferences (when the session has none)
// Mongo, written  matchinputs: one document per (matchId, playerIndex), replaced when sent again: {matchId, playerIndex,
//                 playerId, playerName, character, teamIndex, map, mode, matchType, isCustomGame, gameplayPreferences
//                 (the player's input settings: what a raw input means depends on them), endedBy, startedAt, endedAt,
//                 frameRate, durationFrames, frames, receivedFrames, encoding ("u32le-gzip": one little-endian uint32
//                 per frame, gzipped), inputs (binary), missing ([[from, to]] inclusive, frames never received: 0 in
//                 inputs), droppedInputs (the match's), createdAt}. Context the match notification no longer holds is
//                 left out (null).

public interface IMatchInputs
{
    /// <summary>Stores the recording <paramref name="body"/> sent with <paramref name="matchUpdateKey"/>: the status and the answer.</summary>
    Task<(int Status, JsonObject Answer)> StoreAsync(string? matchUpdateKey, JsonObject? body, CancellationToken ct);
}

internal sealed class MatchInputs(IServiceProvider services, IOptionsMonitor<RollbackSettings> settings, TimeProvider time, ILogger<MatchInputs> log) : IMatchInputs
{
    public const string Collection = "matchinputs";
    private const string Placeholder = "MisconfiguredMatchUpdateKey";

    public async Task<(int Status, JsonObject Answer)> StoreAsync(string? matchUpdateKey, JsonObject? body, CancellationToken ct)
    {
        string configured = settings.CurrentValue.MatchUpdateKey;
        if (configured.Length == 0 || configured == Placeholder)
        {
            log.LogError("Match inputs refused: no Rollback:MatchUpdateKey (MATCHUPDATEKEY) is configured");
            return (403, Error("Invalid signature"));
        }

        if (!KeysMatch(matchUpdateKey, configured))
        {
            log.LogWarning("Match inputs refused: {State} MatchUpdateKey", string.IsNullOrEmpty(matchUpdateKey) ? "missing" : "invalid");
            return (403, Error("Invalid signature"));
        }

        if (body is null || Text(body["matchId"]) is not { } matchId || body["players"] is not JsonArray players || players.Count == 0)
        {
            log.LogWarning("Match inputs refused: no body, matchId or players");
            return (400, Error("Missing matchId or players"));
        }

        var redis = services.GetService<IConnectionMultiplexer>()?.GetDatabase();
        var mongo = services.GetService<IMongoDatabase>();
        if (mongo is null)
        {
            log.LogError("Match inputs of {Match} not stored: this service has no Mongo (MONGODB_URI)", matchId);
            return (500, Error("Not stored"));
        }

        // The match as it was started, while Redis still has it.
        JsonObject? notification = null;
        JsonObject? match = null;
        if (redis is not null)
        {
            notification = Json(await redis.StringGetAsync(matchId)) as JsonObject;
            match = Json(await redis.StringGetAsync($"match:{matchId}")) as JsonObject;
        }

        if (notification is not null && Text(notification["matchKey"]) is { } matchKey && matchKey != Text(body["key"]))
        {
            log.LogWarning("Match inputs of {Match} refused: the match's key differs", matchId);
            return (400, Error("Key mismatch"));
        }

        var now = time.GetUtcNow().UtcDateTime;
        var documents = new List<BsonDocument>();
        foreach (var node in players)
        {
            if (node is not JsonObject player || Number(player["playerIndex"]) is not { } playerIndex
                || Text(player["inputs"]) is not { } inputs || !TryBase64(inputs, out byte[] bytes))
            {
                log.LogWarning("Match inputs of {Match}: a player entry without a playerIndex or inputs; skipped", matchId);
                continue;
            }

            string playerId = Text(player["playerId"]) ?? "";
            var entry = (notification?["players"] as JsonArray)?.OfType<JsonObject>()
                .FirstOrDefault(p => playerId.Length > 0 ? Text(p["playerId"]) == playerId : Number(p["playerIndex"]) == playerIndex);
            documents.Add(new BsonDocument
            {
                { "matchId", matchId },
                { "playerIndex", playerIndex },
                { "playerId", playerId },
                { "playerName", Text(player["playerName"]) ?? "" },
                { "character", Text(player["playerCharacter"]) ?? "" },
                { "teamIndex", Number(entry?["teamIndex"]) is { } team ? team : BsonNull.Value },
                { "map", Text(notification?["map"]) is { } map ? map : BsonNull.Value },
                { "mode", Text(notification?["mode"]) is { } mode ? mode : BsonNull.Value },
                { "matchType", Text(match?["matchType"]) is { } type ? type : BsonNull.Value },
                { "isCustomGame", notification?["isCustomGame"] is JsonValue custom && custom.TryGetValue(out bool isCustom) ? isCustom : BsonNull.Value },
                { "gameplayPreferences", playerId.Length > 0 ? await PreferencesAsync(redis, mongo, playerId, ct) : BsonNull.Value },
                { "endedBy", Text(body["endedBy"]) ?? "" },
                { "startedAt", Date(body["startedAtUtc"]) },
                { "endedAt", Date(body["endedAtUtc"]) },
                { "frameRate", Number(body["frameRate"]) ?? 60 },
                { "durationFrames", Long(body["durationFrames"]) },
                { "frames", Long(player["frames"]) },
                { "receivedFrames", Long(player["receivedFrames"]) },
                { "encoding", Text(player["encoding"]) ?? "" },
                { "inputs", new BsonBinaryData(bytes) },
                { "missing", new BsonArray((player["missing"] as JsonArray ?? []).OfType<JsonArray>()
                    .Select(r => new BsonArray(r.Select(f => (BsonValue)Long(f))))) },
                { "droppedInputs", Long(body["droppedInputs"]) },
                { "createdAt", now },
            });
        }

        if (documents.Count == 0)
        {
            return (400, Error("No usable player entries"));
        }

        try
        {
            var collection = mongo.GetCollection<BsonDocument>(Collection);
            await collection.BulkWriteAsync(documents.Select(d => new ReplaceOneModel<BsonDocument>(
                new BsonDocument { { "matchId", matchId }, { "playerIndex", d["playerIndex"] } }, d) { IsUpsert = true }), cancellationToken: ct);
        }
        catch (Exception e) when (e is MongoException or TimeoutException)
        {
            log.LogError("Match inputs of {Match} not stored: {Error}", matchId, e.Message);
            return (500, Error("Not stored"));
        }

        log.LogInformation("Match inputs of {Match} stored: {Players} player(s) ({Ids}), ended by {EndedBy}, {Bytes} input bytes",
            matchId, documents.Count, string.Join(", ", documents.Select(d => d["playerId"].AsString)), Text(body["endedBy"]),
            documents.Sum(d => d["inputs"].AsBsonBinaryData.Bytes.Length));
        return (200, new JsonObject { ["status"] = "ok" });
    }

    // The player's input settings now (GameplayPreferences: the session's, else the record's), as stored there.
    private static async Task<BsonValue> PreferencesAsync(IDatabase? redis, IMongoDatabase mongo, string playerId, CancellationToken ct)
    {
        if (redis is not null && await redis.HashGetAsync($"connections:{playerId}", "GameplayPreferences") is { HasValue: true } session
            && long.TryParse(session.ToString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long fromSession))
        {
            return fromSession is >= int.MinValue and <= int.MaxValue ? new BsonInt32((int)fromSession) : new BsonInt64(fromSession);
        }

        if (!ObjectId.TryParse(playerId, out var id))
        {
            return BsonNull.Value;
        }

        var record = await mongo.GetCollection<BsonDocument>("playertesters").Find(new BsonDocument("_id", id))
            .Project(new BsonDocument("GameplayPreferences", 1)).FirstOrDefaultAsync(ct);
        return record?.GetValue("GameplayPreferences", BsonNull.Value) ?? BsonNull.Value;
    }

    // As the TS isValidMatchUpdateKey: lower-cased, same length, constant time.
    private static bool KeysMatch(string? provided, string configured)
    {
        if (string.IsNullOrEmpty(provided))
        {
            return false;
        }

        byte[] a = Encoding.UTF8.GetBytes(provided.ToLowerInvariant());
        byte[] b = Encoding.UTF8.GetBytes(configured.ToLowerInvariant());
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static JsonObject Error(string error) => new() { ["error"] = error };

    private static string? Text(JsonNode? value) => value is JsonValue v && v.TryGetValue(out string? s) && s.Length > 0 ? s : null;

    // A JSON number, however the node holds it (parsed, or built from an int).
    private static int? Number(JsonNode? value) =>
        value is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number
        && int.TryParse(v.ToJsonString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int n) ? n : null;

    private static long Long(JsonNode? value) =>
        value is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number
        && long.TryParse(v.ToJsonString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long n) ? n : 0;

    private static BsonValue Date(JsonNode? value) =>
        Text(value) is { } text && DateTime.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var at)
            ? new BsonDateTime(at) : BsonNull.Value;

    private static bool TryBase64(string text, out byte[] bytes)
    {
        try
        {
            bytes = Convert.FromBase64String(text);
            return true;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }

    private static JsonNode? Json(RedisValue value)
    {
        if (!value.HasValue)
        {
            return null;
        }

        try
        {
            return Js.Parse(value.ToString());
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}

public static class MatchInputsHosting
{
    public static WebApplicationBuilder AddMatchInputs(this WebApplicationBuilder builder)
    {
        builder.AddSetting<RollbackSettings>("Rollback");
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IMatchInputs, MatchInputs>();
        return builder;
    }
}

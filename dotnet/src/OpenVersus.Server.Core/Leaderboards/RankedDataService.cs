using System.ComponentModel;
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

namespace OpenVersus.Server.Core.Leaderboards;

// GET /ssc/invoke/ranked_data, ported from the TS server's handleSsc_invoke_ranked_data (handlers/ssc.ts) and
// getOrCreateRating (services/eloService.ts), branch infinity-war: the ranked screen's season data for 1v1 and 2v2, per
// character, from the player's rating and match stats. Anything that fails answers { SeasonalData: {} }, as there.
//
// Mongo, read     eloratings {account_id} (then the rank, as RankService.PlaceAsync); playertesters {_id} character (when
//                 the session names none); playerstats {account_id} characters_1v1, characters_2v2
// Mongo, written  eloratings insert for a player with no rating: _id, account_id, username, elo_1v1, elo_2v2
//                 (Ranked:DefaultElo), wins/losses_1v1/2v2 0, win_streak_1v1/2v2 0, updated_at (ms, a double), __v 0 (the
//                 empty characters maps are not stored: mongoose leaves empty objects out); eloratings updateOne
//                 {account_id} $set username when the token's differs from the stored one
// Redis, read     connections:{id} character

/// <summary>Ranked settings.</summary>
public sealed class RankedSettings
{
    [Description("The rating a player starts with in both modes (DEFAULT_ELO).")]
    public double DefaultElo { get; set; }
}

public interface IRankedDataService
{
    /// <summary>The ranked data of the player the session token (<paramref name="claims"/>) names.</summary>
    Task<JsonNode> DataAsync(JsonObject? claims, CancellationToken ct);
}

internal sealed class RankedDataService(IServiceProvider services, IOptionsMonitor<RankedSettings> settings, TimeProvider time, ILogger<RankedDataService> log) : IRankedDataService
{
    private const string DefaultCharacter = "character_wonder_woman";

    public async Task<JsonNode> DataAsync(JsonObject? claims, CancellationToken ct)
    {
        try
        {
            var mongo = services.GetService<IMongoDatabase>() ?? throw new InvalidOperationException("this service has no Mongo (MONGODB_URI)");
            var ratings = mongo.GetCollection<BsonDocument>("eloratings");
            string? playerId = claims?["id"] is JsonValue v && v.TryGetValue(out string? id) && id.Length > 0 ? id : null;
            string username = claims?["username"] is JsonValue u && u.TryGetValue(out string? name) ? name : "";

            var rating = playerId is null ? null : await GetOrCreateAsync(ratings, playerId, username, ct);
            string character = playerId is null ? "" : await ConnectionCharacterAsync(playerId);
            if (character.Length == 0 && playerId is not null)
            {
                character = await StoredCharacterAsync(mongo, playerId, ct);
            }

            if (character.Length == 0)
            {
                character = DefaultCharacter;
            }

            var stats = playerId is null ? null : await mongo.GetCollection<BsonDocument>("playerstats")
                .Find(new BsonDocument("account_id", playerId)).FirstOrDefaultAsync(ct);
            var place1v1 = playerId is null ? null : await RankService.PlaceAsync(ratings, playerId, "1v1", ct);
            var place2v2 = playerId is null ? null : await RankService.PlaceAsync(ratings, playerId, "2v2", ct);
            // Math.floor(Date.now() / 1000), for every timestamp in the answer.
            long now = time.GetUtcNow().ToUnixTimeMilliseconds() / 1000;

            JsonObject Mode(string mode, (BsonValue Rating, long Rank)? place)
            {
                double elo = Number(rating, $"elo_{mode}"), wins = Number(rating, $"wins_{mode}"), losses = Number(rating, $"losses_{mode}");
                return ModeData(elo, wins, losses, wins + losses, place?.Rank ?? 0, Map(rating, $"characters_{mode}"), Map(stats, $"characters_{mode}"), character, now);
            }

            return new JsonObject
            {
                ["body"] = new JsonObject
                {
                    ["SeasonalData"] = new JsonObject
                    {
                        ["Season:SeasonFive"] = new JsonObject
                        {
                            ["Ranked"] = new JsonObject
                            {
                                ["DataByMode"] = new JsonObject { ["1v1"] = Mode("1v1", place1v1), ["2v2"] = Mode("2v2", place2v2) },
                                ["ClaimedRewards"] = new JsonArray(),
                                ["bEndOfSeasonRewardsGranted"] = false,
                            },
                        },
                    },
                },
                ["metadata"] = null,
                ["return_code"] = 0,
            };
        }
        catch (Exception e) when (e is MongoException or RedisException or TimeoutException or FormatException or InvalidOperationException)
        {
            log.LogError("Error in ranked_data: {Error}", e.Message);
            return new JsonObject { ["body"] = new JsonObject { ["SeasonalData"] = new JsonObject() }, ["metadata"] = null, ["return_code"] = 0 };
        }
    }

    // buildModeData.
    private static JsonObject ModeData(double elo, double wins, double losses, double games, long rank, BsonDocument charMap, BsonDocument psCharMap, string character, long now)
    {
        var stamp = () => new JsonObject { ["_hydra_unix_date"] = now };
        // No games and no characters: the game shows Unranked.
        if (games == 0 && charMap.ElementCount == 0)
        {
            return new JsonObject
            {
                ["BestCharacter"] = new JsonObject { ["CurrentPoints"] = 0, ["MaxPoints"] = 0, ["GamesPlayed"] = 0, ["SetsPlayed"] = 0, ["CharacterSlug"] = "", ["LastUpdateTimestamp"] = stamp() },
                ["DataByCharacter"] = new JsonObject(),
                ["GamesPlayed"] = 0,
                ["LastUpdateTimestamp"] = stamp(),
                ["SetsPlayed"] = 0,
                ["FinalLeaderboardRank"] = 0,
            };
        }

        var byCharacter = new JsonObject();
        string bestChar = character;
        double bestElo = -1; // any character with a rating wins
        foreach (var entry in Js.OrderedLikeAnObject(charMap, e => e.Name))
        {
            // (data as any).elo: a character entry that is not an object throws there, and the answer falls back.
            var data = entry.Value as BsonDocument ?? throw new FormatException($"character {entry.Name} is a {entry.Value.BsonType}");
            double charElo = Number(data, "elo"), charWins = Number(data, "wins"), charLosses = Number(data, "losses");
            var psc = Map(psCharMap, entry.Name);
            byCharacter[entry.Name] = new JsonObject
            {
                ["CurrentPoints"] = charElo,
                ["MaxPoints"] = charElo,
                ["GamesPlayed"] = charWins + charLosses,
                ["SetsPlayed"] = charWins + charLosses,
                ["Wins"] = charWins,
                ["Losses"] = charLosses,
                ["DamageDealt"] = Math.Floor(Number(psc, "totalDamageDealt") + 0.5),
                ["DamageTaken"] = 0,
                ["Ringouts"] = Number(psc, "ringouts"),
                ["Deaths"] = 0,
                ["LastUpdateTimestamp"] = stamp(),
                ["LastDecayMs"] = 0,
            };
            if (charElo > bestElo)
            {
                bestElo = charElo;
                bestChar = entry.Name;
            }
        }

        // No per-character data yet: the current character stands in (and BestCharacter keeps -1 points, as there).
        if (byCharacter.Count == 0)
        {
            var psc = Map(psCharMap, character);
            byCharacter[character] = new JsonObject
            {
                ["CurrentPoints"] = elo,
                ["MaxPoints"] = elo,
                ["GamesPlayed"] = games,
                ["SetsPlayed"] = wins + losses,
                ["Wins"] = wins,
                ["Losses"] = losses,
                ["DamageDealt"] = Math.Floor(Number(psc, "totalDamageDealt") + 0.5),
                ["DamageTaken"] = 0,
                ["Ringouts"] = Number(psc, "ringouts"),
                ["Deaths"] = 0,
                ["LastUpdateTimestamp"] = stamp(),
                ["LastDecayMs"] = 0,
            };
        }

        return new JsonObject
        {
            ["BestCharacter"] = new JsonObject
            {
                ["CurrentPoints"] = bestElo,
                ["MaxPoints"] = bestElo,
                ["GamesPlayed"] = games,
                ["SetsPlayed"] = wins + losses,
                ["CharacterSlug"] = bestChar,
                ["LastUpdateTimestamp"] = stamp(),
            },
            ["DataByCharacter"] = byCharacter,
            ["GamesPlayed"] = games,
            ["LastUpdateTimestamp"] = stamp(),
            ["SetsPlayed"] = wins + losses,
            ["FinalLeaderboardRank"] = rank,
        };
    }

    private async Task<BsonDocument> GetOrCreateAsync(IMongoCollection<BsonDocument> ratings, string playerId, string username, CancellationToken ct)
    {
        var filter = new BsonDocument("account_id", playerId);
        var rating = await ratings.Find(filter).FirstOrDefaultAsync(ct);
        if (rating is null)
        {
            var elo = JsNumber(settings.CurrentValue.DefaultElo);
            rating = new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() },
                { "account_id", playerId },
                { "username", username },
                { "elo_1v1", elo },
                { "elo_2v2", elo },
                { "wins_1v1", 0 },
                { "losses_1v1", 0 },
                { "wins_2v2", 0 },
                { "losses_2v2", 0 },
                { "win_streak_1v1", 0 },
                { "win_streak_2v2", 0 },
                { "updated_at", (double)time.GetUtcNow().ToUnixTimeMilliseconds() },
                { "__v", 0 },
            };
            // Two first requests at once: the unique index refuses the second, which falls back, as there.
            await ratings.InsertOneAsync(rating, cancellationToken: ct);
            log.LogInformation("Created new ELO rating for player {Player} ({Username})", playerId, username);
        }
        else if (username.Length > 0 && username != (rating.GetValue("username", "") is BsonString stored ? stored.Value : ""))
        {
            await ratings.UpdateOneAsync(filter, new BsonDocument("$set", new BsonDocument("username", username)), cancellationToken: ct);
        }

        return rating;
    }

    private async Task<string> ConnectionCharacterAsync(string playerId)
    {
        if (services.GetService<IConnectionMultiplexer>()?.GetDatabase() is not { } redis)
        {
            throw new InvalidOperationException("this service has no Redis (REDIS)");
        }

        var value = await redis.HashGetAsync($"connections:{playerId}", "character");
        return value.HasValue ? value.ToString() : "";
    }

    // PlayerTesterModel.findById(playerId): an id that is no ObjectId, or any failure, is no character (a caught error there).
    private static async Task<string> StoredCharacterAsync(IMongoDatabase mongo, string playerId, CancellationToken ct)
    {
        if (!ObjectId.TryParse(playerId, out var id))
        {
            return "";
        }

        try
        {
            var player = await mongo.GetCollection<BsonDocument>("playertesters").Find(new BsonDocument("_id", id))
                .Project(new BsonDocument("character", 1)).FirstOrDefaultAsync(ct);
            return player?.GetValue("character", BsonNull.Value) is BsonString { Value.Length: > 0 } s ? s.Value : "";
        }
        catch (MongoException)
        {
            return "";
        }
    }

    // x || {}: the document stored there, or an empty one.
    private static BsonDocument Map(BsonDocument? doc, string field) =>
        doc?.GetValue(field, BsonNull.Value) as BsonDocument ?? [];

    // x || 0 for a number (a stored non-number is refused: none exists, and TS would carry it on as it is).
    private static double Number(BsonDocument? doc, string field) => doc?.GetValue(field, BsonNull.Value) switch
    {
        null or BsonNull or BsonUndefined => 0,
        { IsNumeric: true } n => double.IsNaN(n.ToDouble()) ? 0 : n.ToDouble(),
        var other => throw new FormatException($"{field} is a {other.BsonType}"),
    };

    // A JS number as the TS server's driver stores it: an int32 when it is whole and fits, else a double.
    private static BsonValue JsNumber(double value) =>
        value == Math.Floor(value) && value is >= int.MinValue and <= int.MaxValue && !(value == 0 && double.IsNegative(value)) ? new BsonInt32((int)value) : new BsonDouble(value);
}

public static class RankedDataHosting
{
    public static WebApplicationBuilder AddRankedData(this WebApplicationBuilder builder)
    {
        builder.AddSetting<RankedSettings>("Ranked");
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IRankedDataService, RankedDataService>();
        return builder;
    }
}

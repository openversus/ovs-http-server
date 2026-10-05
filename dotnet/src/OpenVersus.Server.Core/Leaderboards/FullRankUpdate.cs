using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Compat;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Leaderboards;

// The FullRankUpdate a player's game is sent when a ranked set it played is over, ported from the TS websocket's
// handleOnMatchEnd (websocket.ts, branch infinity-war): per mode, the player's rating, games and place, and an entry per
// fighter they have a rating for, with that fighter's damage and ring-outs from playerstats.
//
//   per mode (1v1, 2v2): no games and no fighter ratings: zeros, BestCharacter "" ; otherwise DataByCharacter has each
//     rated fighter (its rating as CurrentPoints and MaxPoints, wins + losses as GamesPlayed and SetsPlayed, damage
//     rounded), BestCharacter the highest-rated; with games but no fighter rated, one entry for the player's fighter
//     (connections:{player} character, else playertesters' character, else character_wonder_woman) carrying the mode's
//     rating, and BestCharacter's points -1 (as there: the highest of no fighters). FinalLeaderboardRank: the place in
//     the mode (RankService), 0 without games.
//
// Redis, read     connections:{player} character
// Mongo, read     eloratings (made when missing, as getOrCreateRating), playerstats, playertesters
//
// Unlike there: the season is Season:Current (TS: always Season:SeasonFive), as ranked_data and the login.

public static class FullRankUpdate
{
    /// <summary>The message for <paramref name="playerId"/> (a profile-notification), in <paramref name="season"/>.</summary>
    public static async Task<JsonObject> BuildAsync(IDatabase redis, IMongoDatabase mongo, EloRatings ratings, string playerId, string season, TimeProvider time, CancellationToken ct)
    {
        var rating = await ratings.GetOrCreateAsync(mongo.GetCollection<BsonDocument>("eloratings"), playerId, "", ct);
        var eloratings = mongo.GetCollection<BsonDocument>("eloratings");
        var rank1v1 = await RankService.PlaceAsync(eloratings, playerId, "1v1", ct);
        var rank2v2 = await RankService.PlaceAsync(eloratings, playerId, "2v2", ct);
        var stats = await mongo.GetCollection<BsonDocument>("playerstats").Find(new BsonDocument("account_id", playerId)).FirstOrDefaultAsync(ct);
        string character = await CharacterAsync(redis, mongo, playerId, ct);
        long now = time.GetUtcNow().ToUnixTimeMilliseconds() / 1000;

        JsonObject Mode(string mode, (BsonValue Rating, long Rank)? place) => BuildMode(
            Or(rating, $"elo_{mode}"), Or(rating, $"wins_{mode}"), Or(rating, $"losses_{mode}"), place?.Rank ?? 0,
            Map(rating, $"characters_{mode}"), Map(stats, $"characters_{mode}"), character, now);

        return new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["template_id"] = "FullRankUpdate",
                ["SeasonalData"] = new JsonObject
                {
                    [season] = new JsonObject
                    {
                        ["Ranked"] = new JsonObject
                        {
                            ["DataByMode"] = new JsonObject { ["1v1"] = Mode("1v1", rank1v1), ["2v2"] = Mode("2v2", rank2v2) },
                            ["ClaimedRewards"] = new JsonArray(),
                            ["bEndOfSeasonRewardsGranted"] = false,
                        },
                    },
                },
            },
            ["payload"] = new JsonObject
            {
                ["frm"] = new JsonObject { ["id"] = "internal-server", ["type"] = "server-api-key" },
                ["template"] = "realtime",
                ["account_id"] = playerId,
                ["profile_id"] = playerId,
            },
            ["header"] = "",
            ["cmd"] = "profile-notification",
        };
    }

    private static JsonObject BuildMode(double elo, double wins, double losses, long rank, BsonDocument characters, BsonDocument statsByCharacter,
        string character, long now)
    {
        JsonObject Stamp() => new() { ["_hydra_unix_date"] = now };
        if (wins + losses == 0 && characters.ElementCount == 0)
        {
            return new JsonObject
            {
                ["BestCharacter"] = new JsonObject
                {
                    ["CurrentPoints"] = 0, ["MaxPoints"] = 0, ["GamesPlayed"] = 0, ["SetsPlayed"] = 0, ["CharacterSlug"] = "", ["LastUpdateTimestamp"] = Stamp(),
                },
                ["DataByCharacter"] = new JsonObject(),
                ["GamesPlayed"] = 0,
                ["LastUpdateTimestamp"] = Stamp(),
                ["SetsPlayed"] = 0,
                ["FinalLeaderboardRank"] = 0,
            };
        }

        var byCharacter = new JsonObject();
        string best = character;
        double bestElo = -1;
        foreach (var entry in Js.OrderedLikeAnObject(characters, e => e.Name))
        {
            var data = entry.Value as BsonDocument ?? [];
            double ce = Or(data, "elo"), cw = Or(data, "wins"), cl = Or(data, "losses");
            var played = statsByCharacter.GetValue(entry.Name, BsonNull.Value) as BsonDocument ?? [];
            byCharacter[entry.Name] = Character(ce, cw, cl, Or(played, "totalDamageDealt"), Or(played, "ringouts"), Stamp());
            if (ce > bestElo)
            {
                bestElo = ce;
                best = entry.Name;
            }
        }

        if (byCharacter.Count == 0)
        {
            var played = statsByCharacter.GetValue(character, BsonNull.Value) as BsonDocument ?? [];
            byCharacter[character] = Character(elo, wins, losses, Or(played, "totalDamageDealt"), Or(played, "ringouts"), Stamp());
        }

        return new JsonObject
        {
            ["BestCharacter"] = new JsonObject
            {
                ["CurrentPoints"] = Number(bestElo), ["MaxPoints"] = Number(bestElo), ["GamesPlayed"] = Number(wins + losses), ["SetsPlayed"] = Number(wins + losses),
                ["CharacterSlug"] = best, ["LastUpdateTimestamp"] = Stamp(),
            },
            ["DataByCharacter"] = byCharacter,
            ["GamesPlayed"] = Number(wins + losses),
            ["LastUpdateTimestamp"] = Stamp(),
            ["SetsPlayed"] = Number(wins + losses),
            ["FinalLeaderboardRank"] = rank,
        };
    }

    private static JsonObject Character(double points, double wins, double losses, double damage, double ringouts, JsonObject stamp) => new()
    {
        ["CurrentPoints"] = Number(points), ["MaxPoints"] = Number(points),
        ["GamesPlayed"] = Number(wins + losses), ["SetsPlayed"] = Number(wins + losses),
        ["Wins"] = Number(wins), ["Losses"] = Number(losses),
        ["DamageDealt"] = Number(Math.Floor(damage + 0.5)), ["DamageTaken"] = 0, ["Ringouts"] = Number(ringouts), ["Deaths"] = 0,
        ["LastUpdateTimestamp"] = stamp,
        ["LastDecayMs"] = 0,
    };

    // The player's fighter: their connection's, else playertesters', else Wonder Woman ("" when the lookup fails).
    private static async Task<string> CharacterAsync(IDatabase redis, IMongoDatabase mongo, string playerId, CancellationToken ct)
    {
        if ((string?)await redis.HashGetAsync($"connections:{playerId}", "character") is { Length: > 0 } character)
        {
            return character;
        }

        if (!ObjectId.TryParse(playerId, out var id))
        {
            return "";
        }

        var tester = await mongo.GetCollection<BsonDocument>("playertesters").Find(new BsonDocument("_id", id)).FirstOrDefaultAsync(ct);
        return tester?.GetValue("character", BsonNull.Value) is BsonString { Value.Length: > 0 } stored ? stored.Value : "character_wonder_woman";
    }

    // doc[field] || 0, as a JS number.
    private static double Or(BsonDocument? doc, string field) =>
        doc?.GetValue(field, BsonNull.Value) is { IsNumeric: true } v && v.ToDouble() is var d && !double.IsNaN(d) ? d : 0;

    private static BsonDocument Map(BsonDocument? doc, string field) => doc?.GetValue(field, BsonNull.Value) as BsonDocument ?? [];

    private static JsonNode Number(double d) => d == Math.Floor(d) && Math.Abs(d) < 9007199254740992 ? JsonValue.Create((long)d) : JsonValue.Create(d);
}

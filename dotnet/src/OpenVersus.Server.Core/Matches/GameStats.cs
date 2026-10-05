using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Leaderboards;
using StackExchange.Redis;
using ZstdSharp;

namespace OpenVersus.Server.Core.Matches;

// One game's stats into the players' records, ported from the TS server's recordGameStats (services/statsService.ts),
// branch infinity-war, run by the match flow once per match (MatchResultStream) from the report that agrees with the
// decided winner.
//
// Per player in the report's PlayerMissionUpdates with a character (match_characters:{match}, else connections:{id}
// character; none: skipped), in the mode's characters_{mode}.{slug}: ringouts and totalDamageDealt ($inc) and
// highestDamageDealt ($max) from TotalRingouts and TotalDamageDealt (else TotalAttackDamageDealt); aggregate.{stat}
// ($inc) for every Stat:Game:Character:* above 0 ("Stat:Game:Character:Stock:DamageTaken" -> stockDamageTaken);
// fighterStats.{name} ($inc) for every Fighter:/Hitbox:/Marceline: counter above 0; and the game on recent_matches_{mode}
// ($push, the last 10): {matchId, timestamp, mode, map, result, score, players: [{accountId, character, teamIndex,
// damage, ringouts, deaths (the other teams' ringouts), isWinner}]}. Every number rounded as JavaScript does.
//
// Mongo, written  playerstats {account_id} (upsert; $setOnInsert: __v 0 and the schema's defaults for the paths the
//                 update does not write, as mongoose sends it); match_archives {match_id} (upsert, $setOnInsert:
//                 {match_id, timestamp (a date), compressed_data (zstd level 9 of the JSON {match_id, mode, is_custom,
//                 timestamp, winning_team, score, players: [{account_id, character}], network_stats, mission_updates}),
//                 __v 0})
// Redis, read     {match} (the config: mode, map, players' teams, isCustomGame); match_characters:{match};
//                 connections:{id} character
// Both keys live 20 minutes from the match's start: stats recorded later than that lose the characters and teams.
//
// Unlike there: a whole number the game sent as a Hydra double ({_hydra_double: n}; damage and knockback counters that
// happen to land on a whole number) counts as that number. TS skipped it (not a JavaScript number), so those games lost
// the counter, and a whole TotalDamageDealt fell back to the attack damage. The archive keeps the report as received.

public interface IGameStats
{
    /// <summary>Records game <paramref name="matchId"/>'s stats from one report's EndOfMatchStats.</summary>
    Task RecordAsync(string matchId, JsonObject endOfMatchStats, CancellationToken ct);
}

internal sealed class GameStats(IServiceProvider services, TimeProvider time, ILogger<GameStats> log) : IGameStats
{
    public const string ArchiveCollection = "match_archives";
    private static readonly string[] s_defaults = ["recent_matches_1v1", "recent_matches_2v2", "characters_1v1", "characters_2v2", "aggregate"];

    public async Task RecordAsync(string matchId, JsonObject endOfMatchStats, CancellationToken ct)
    {
        if (endOfMatchStats["PlayerMissionUpdates"] is not JsonObject pmu)
        {
            return;
        }

        var mongo = services.GetService<IMongoDatabase>() ?? throw new InvalidOperationException("this service has no Mongo (MONGODB_URI)");
        var redis = services.GetService<IConnectionMultiplexer>()?.GetDatabase() ?? throw new InvalidOperationException("this service has no Redis (REDIS)");
        var config = Js.Parse((string?)await redis.StringGetAsync(matchId) ?? "null") as JsonObject;
        string mode = Text(config?["mode"]) is { Length: > 0 } m ? m : "1v1";
        bool is1v1 = mode == "1v1" || mode.Contains("1v1", StringComparison.Ordinal);
        string charsField = is1v1 ? "characters_1v1" : "characters_2v2";
        string recentField = is1v1 ? "recent_matches_1v1" : "recent_matches_2v2";
        var matchChars = Js.Parse((string?)await redis.StringGetAsync($"match_characters:{matchId}") ?? "null") as JsonObject ?? [];
        double winningTeam = MatchWinner.Number(endOfMatchStats["WinningTeamIndex"]) ?? -1;
        var teams = new Dictionary<string, double>();
        foreach (var p in config?["players"] as JsonArray ?? [])
        {
            if (Text(p?["playerId"]) is { Length: > 0 } pid && p!["teamIndex"] is { } t && MatchWinner.Number(t) is { } team)
            {
                teams[pid] = team;
            }
        }

        // Every player's line of the game, as recent_matches holds it (deaths: the other teams' ringouts).
        var lines = pmu.Where(e => e.Value is JsonObject).Select(e =>
        {
            var p = (JsonObject)e.Value!;
            double team = teams.TryGetValue(e.Key, out double t) ? t : -1;
            return (Id: e.Key, Character: Text(matchChars[e.Key]) is { Length: > 0 } c ? c : "unknown", Team: team, Damage: Damage(p), Ringouts: Round(Num(p["Stat:Game:Character:TotalRingouts"])),
                Won: winningTeam >= 0 && team == winningTeam);
        }).ToList();
        var recentPlayers = new BsonArray(lines.Select(l => new BsonDocument
        {
            { "accountId", l.Id },
            { "character", l.Character },
            { "teamIndex", EloRatings.JsNumber(l.Team) },
            { "damage", EloRatings.JsNumber(l.Damage) },
            { "ringouts", EloRatings.JsNumber(l.Ringouts) },
            { "deaths", EloRatings.JsNumber(lines.Where(o => o.Team != l.Team).Sum(o => o.Ringouts)) },
            { "isWinner", l.Won },
        }));
        var score = endOfMatchStats["Score"] is JsonArray raw
            ? new BsonArray { EloRatings.JsNumber(MatchWinner.Number(raw.ElementAtOrDefault(0)) ?? 0), EloRatings.JsNumber(MatchWinner.Number(raw.ElementAtOrDefault(1)) ?? 0) }
            : new BsonArray { 0, 0 };

        var stats = mongo.GetCollection<BsonDocument>("playerstats");
        var updates = new List<Task>();
        foreach (var (accountId, node) in pmu)
        {
            if (node is not JsonObject playerPmu)
            {
                continue;
            }

            string? character = Text(matchChars[accountId]) is { Length: > 0 } stored ? stored
                : (string?)await redis.HashGetAsync($"connections:{accountId}", "character") is { Length: > 0 } live ? live : null;
            if (character is null)
            {
                continue;
            }

            string charPath = $"{charsField}.{character}";
            var inc = new BsonDocument();
            var max = new BsonDocument();
            double ringouts = Round(Num(playerPmu["Stat:Game:Character:TotalRingouts"]));
            double damage = Damage(playerPmu);
            if (ringouts > 0)
            {
                inc[$"{charPath}.ringouts"] = EloRatings.JsNumber(ringouts);
            }

            if (damage > 0)
            {
                inc[$"{charPath}.totalDamageDealt"] = EloRatings.JsNumber(damage);
                max[$"{charPath}.highestDamageDealt"] = EloRatings.JsNumber(damage);
            }

            foreach (var (key, value) in playerPmu)
            {
                if (key.StartsWith("Stat:Game:Character:", StringComparison.Ordinal) && MatchWinner.Number(value) is > 0 and var v && AggregateKey(key) is { Length: > 0 } field)
                {
                    inc[$"aggregate.{field}"] = EloRatings.JsNumber(SetRatings.JsRound(v));
                }
            }

            foreach (var (key, value) in playerPmu)
            {
                if (MatchWinner.Number(value) is > 0 and var v && FighterStat(key) is { } name)
                {
                    inc[$"{charPath}.fighterStats.{name}"] = EloRatings.JsNumber(SetRatings.JsRound(v));
                }
            }

            var line = lines.First(l => l.Id == accountId);
            double myTeam = line.Team;
            var entry = new BsonDocument
            {
                { "matchId", matchId },
                { "timestamp", (double)time.GetUtcNow().ToUnixTimeMilliseconds() },
                { "mode", mode },
                { "map", Text(config?["map"]) is { Length: > 0 } map ? map : "" },
                { "result", winningTeam >= 0 && myTeam == winningTeam ? "win" : "loss" },
                { "score", score.DeepClone() },
                { "players", recentPlayers.DeepClone() },
            };

            // As mongoose sends it: $setOnInsert (__v and the defaults of what the update leaves alone), $max, $inc, $push, $set.
            var touched = new List<string> { recentField };
            touched.AddRange(inc.Names);
            touched.AddRange(max.Names);
            var onInsert = new BsonDocument("__v", 0);
            foreach (string path in s_defaults.Where(d => !touched.Any(t => t == d || t.StartsWith(d + ".", StringComparison.Ordinal))))
            {
                onInsert[path] = path.StartsWith("recent", StringComparison.Ordinal) ? new BsonArray() : new BsonDocument();
            }

            var update = new BsonDocument("$setOnInsert", onInsert);
            if (max.ElementCount > 0)
            {
                update["$max"] = max;
            }

            if (inc.ElementCount > 0)
            {
                update["$inc"] = inc;
            }

            update["$push"] = new BsonDocument(recentField, new BsonDocument { { "$each", new BsonArray { entry } }, { "$slice", -10 } });
            update["$set"] = new BsonDocument("updated_at", (double)time.GetUtcNow().ToUnixTimeMilliseconds());
            updates.Add(UpdateAsync(stats, accountId, update, ct));
        }

        // The archive: one compressed document per game, the first one kept.
        var archive = new JsonObject
        {
            ["match_id"] = matchId,
            ["mode"] = mode,
            ["is_custom"] = config?["isCustomGame"] is JsonValue custom && custom.GetValueKind() == System.Text.Json.JsonValueKind.True,
            ["timestamp"] = time.GetUtcNow().ToUnixTimeMilliseconds(),
        };
        // JSON.stringify leaves out what is undefined.
        if (endOfMatchStats.ContainsKey("WinningTeamIndex"))
        {
            archive["winning_team"] = endOfMatchStats["WinningTeamIndex"]?.DeepClone();
        }

        if (endOfMatchStats.ContainsKey("Score"))
        {
            archive["score"] = endOfMatchStats["Score"]?.DeepClone();
        }

        archive["players"] = new JsonArray([.. pmu.Select(e => (JsonNode)new JsonObject { ["account_id"] = e.Key, ["character"] = Text(matchChars[e.Key]) is { Length: > 0 } c ? c : "unknown" })]);
        if (endOfMatchStats.ContainsKey("PlayerNetworkStats"))
        {
            archive["network_stats"] = endOfMatchStats["PlayerNetworkStats"]?.DeepClone();
        }

        archive["mission_updates"] = pmu.DeepClone();
        byte[] compressed;
        using (var compressor = new Compressor(9))
        {
            compressed = compressor.Wrap(Encoding.UTF8.GetBytes(Js.Stringify(archive))).ToArray();
        }

        updates.Add(ArchiveAsync(mongo, matchId, compressed, ct));
        await Task.WhenAll(updates);
        log.LogInformation("Recorded game stats for {Count} player(s), match {Match} ({Bytes} bytes archived)", pmu.Count, matchId, compressed.Length);
    }

    private async Task UpdateAsync(IMongoCollection<BsonDocument> stats, string accountId, BsonDocument update, CancellationToken ct)
    {
        try
        {
            await stats.UpdateOneAsync(new BsonDocument("account_id", accountId), update, new UpdateOptions { IsUpsert = true }, ct);
        }
        catch (Exception e) when (e is MongoException or TimeoutException)
        {
            log.LogError("Failed to update game stats for {Player}: {Error}", accountId, e.Message);
        }
    }

    private async Task ArchiveAsync(IMongoDatabase mongo, string matchId, byte[] compressed, CancellationToken ct)
    {
        try
        {
            await mongo.GetCollection<BsonDocument>(ArchiveCollection).UpdateOneAsync(new BsonDocument("match_id", matchId),
                new BsonDocument("$setOnInsert", new BsonDocument
                {
                    { "match_id", matchId },
                    { "timestamp", time.GetUtcNow().UtcDateTime },
                    { "compressed_data", new BsonBinaryData(compressed) },
                    { "__v", 0 },
                }), new UpdateOptions { IsUpsert = true }, ct);
        }
        catch (Exception e) when (e is MongoException or TimeoutException)
        {
            log.LogError("Failed to archive {Match}: {Error}", matchId, e.Message);
        }
    }

    // Number(TotalDamageDealt) || Number(TotalAttackDamageDealt) || 0, rounded.
    private static double Damage(JsonObject pmu) =>
        Round(Num(pmu["Stat:Game:Character:TotalDamageDealt"]) is var d && d != 0 && !double.IsNaN(d) ? d : Num(pmu["Stat:Game:Character:TotalAttackDamageDealt"]));

    // Math.round(x || 0).
    private static double Round(double value) => double.IsNaN(value) ? 0 : SetRatings.JsRound(value);

    private static double Num(JsonNode? value) => MatchWinner.Number(value) ?? double.NaN;

    /// <summary>"Stat:Game:Character:Stock:DamageTaken" -> "stockDamageTaken"; "" for any other key.</summary>
    internal static string AggregateKey(string key)
    {
        const string prefix = "Stat:Game:Character:";
        if (!key.StartsWith(prefix, StringComparison.Ordinal))
        {
            return "";
        }

        string rest = key[prefix.Length..].Replace(":", "", StringComparison.Ordinal);
        return rest.Length == 0 ? "" : char.ToLowerInvariant(rest[0]) + rest[1..];
    }

    /// <summary>A character's own counter: "Fighter:{X}:{name}" and "Hitbox:{X}:{name}" -> name, "Marceline:{name}" -> name.</summary>
    internal static string? FighterStat(string key)
    {
        if (key.StartsWith("Fighter:", StringComparison.Ordinal) || key.StartsWith("Hitbox:", StringComparison.Ordinal))
        {
            string[] parts = key.Split(':');
            return parts.Length >= 3 ? string.Join(':', parts[2..]) : null;
        }

        return key.StartsWith("Marceline:", StringComparison.Ordinal) ? key["Marceline:".Length..] : null;
    }

    private static string? Text(JsonNode? value) => value is JsonValue v && v.TryGetValue(out string? s) ? s : null;
}

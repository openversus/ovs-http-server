using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Compat;

namespace OpenVersus.Server.Core.Matches;

// A player's recent matches, ported from the TS server's GET /matches/all/:id (handlers/matches.ts handleMatches_all_id
// and buildMatchResponse, branch infinity-war).
//
// Mongo, read   playerstats { account_id, recent_matches_1v1: [entry], recent_matches_2v2: [entry] }, where an entry is
//               { matchId, timestamp (ms), mode "1v1"|"2v2"|"FFA", map, result "win"|"loss", score: [team 0, team 1],
//                 players: [{ accountId, character, teamIndex, damage, ringouts, deaths, isWinner }] }
//               (written by the TS statsService recordGameStats; FFA entries are in recent_matches_2v2)
//               playertesters { _id, name }: the players' names
// Nothing written.
//
// Corrected here (same keys and types on the wire; the TS server sends the first of each):
//   - identity.username and the steam username: "" -> the player's name (playertesters.name; "" when unknown)
//   - win / loss: [me] or [first other player] -> the players marked isWinner / the rest (a 2v2 teammate was listed as
//     the opponent)
//   - WinningTeamIndex / winning_team: 0 when I won, else 1 -> the winners' teamIndex (it assumed I was team 0)
//   - ModeString and template.data.mode: always "1v1" -> the entry's mode
//   - criteria, name and template slug: FFA -> ffa_container (was 2v2_container; the game asks for ffa_container);
//     template max_players: always 2 -> the mode's player count (2, 4, 4)
// An entry with no player marked isWinner (none stored so far) keeps the TS server's win / loss and team.
//
// As there: the route's id is the player (no "me"); the game's fields and templates query values are not read; a match
// that cannot be built is left out and the rest are sent; any other failure answers an empty first page. Each match
// has a new rand and template id every time. Unlike there, an entry whose fields have a type the stats writer never
// stores (a timestamp that is not a number, a mode that is not a string, a player that is not a document, a field that
// is missing from a player) is left out, where the TS server would build a match from whatever JavaScript makes of it,
// and an entry that is not a document fails the request, as there; a census of every stored entry found none of these.

public interface IMatchHistoryService
{
    /// <summary>
    /// The recent-matches page for <paramref name="accountId"/>: <c>{matches, total_matches, current_page, total_pages}</c>,
    /// oldest first; <paramref name="count"/> and <paramref name="page"/> as the query sent them (repeated values joined
    /// with commas). Null when this service has no Mongo.
    /// </summary>
    Task<JsonObject?> AllAsync(string accountId, string? count, string? page, CancellationToken ct = default);
}

internal sealed class MatchHistoryService(IServiceProvider services, TimeProvider time, ILogger<MatchHistoryService> log) : IMatchHistoryService
{
    public async Task<JsonObject?> AllAsync(string accountId, string? count, string? page, CancellationToken ct)
    {
        if (services.GetService<IMongoDatabase>() is not { } mongo)
        {
            return null;
        }

        try
        {
            var stats = await mongo.GetCollection<BsonDocument>("playerstats").Find(new BsonDocument("account_id", accountId)).FirstOrDefaultAsync(ct);
            return Page(accountId, stats, count, page, await NamesAsync(mongo, accountId, stats, ct), time.GetUtcNow(), Random.Shared, log);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log.LogError(e, "/matches/all/{Account} error", accountId);
            return new JsonObject { ["matches"] = new JsonArray(), ["total_matches"] = 0, ["current_page"] = 1, ["total_pages"] = 1 };
        }
    }

    /// <summary>The names of the player and everyone in their stored matches (at most 20 matches of 4), by account id.</summary>
    private static async Task<Dictionary<string, string>> NamesAsync(IMongoDatabase mongo, string accountId, BsonDocument? stats, CancellationToken ct)
    {
        var ids = new HashSet<ObjectId>();
        foreach (var field in new[] { "recent_matches_1v1", "recent_matches_2v2" })
        {
            if (stats?.GetValue(field, BsonNull.Value) is not BsonArray entries)
            {
                continue;
            }

            foreach (var player in entries.OfType<BsonDocument>().SelectMany(e => e.GetValue("players", BsonNull.Value) as BsonArray ?? []).OfType<BsonDocument>())
            {
                if (player.GetValue("accountId", BsonNull.Value) is BsonString { Value: var id } && ObjectId.TryParse(id, out var oid))
                {
                    ids.Add(oid);
                }
            }
        }

        if (ObjectId.TryParse(accountId, out var me))
        {
            ids.Add(me);
        }

        var names = new Dictionary<string, string>();
        if (ids.Count == 0)
        {
            return names;
        }

        var players = await mongo.GetCollection<BsonDocument>(Access.PlayerRecord.Collection)
            .Find(new BsonDocument("_id", new BsonDocument("$in", new BsonArray(ids)))).Project(new BsonDocument("name", 1)).ToListAsync(ct);
        foreach (var player in players)
        {
            if (player.GetValue("name", BsonNull.Value) is BsonString { Value: var name })
            {
                names[player["_id"].ToString()!] = name;
            }
        }

        return names;
    }

    /// <summary>The page built from the player's stats document (null when there is none) and the players' names.</summary>
    internal static JsonObject Page(string accountId, BsonDocument? stats, string? countText, string? pageText, IReadOnlyDictionary<string, string> names, DateTimeOffset now, Random random, ILogger log)
    {
        // [...(stats?.recent_matches_1v1 || []), ...(stats?.recent_matches_2v2 || [])], then a stable sort by
        // (timestamp || 0), oldest first.
        var entries = List(stats, "recent_matches_1v1").Concat(List(stats, "recent_matches_2v2"))
            .Select(e => e is BsonDocument doc ? doc : throw new FormatException($"a {e.BsonType} match entry"))
            .OrderBy(e => SortKey(e.GetValue("timestamp", BsonNull.Value)))
            .ToList();

        // parseInt(count) || 20, parseInt(page) || 1, then entries.slice((page - 1) * count, page * count).
        double count = Js.ParseInt(countText) is var c && c != 0 && !double.IsNaN(c) ? c : 20;
        double page = Js.ParseInt(pageText) is var p && p != 0 && !double.IsNaN(p) ? p : 1;
        var (start, end) = Js.SliceBounds(entries.Count, (page - 1) * count, page * count);

        var matches = new JsonArray();
        for (int i = start; i < end; i++)
        {
            try
            {
                matches.Add(Match(accountId, entries[i], names, now, random));
            }
            catch (Exception e) when (e is FormatException or InvalidCastException or ArgumentOutOfRangeException)
            {
                log.LogError("Failed to build match entry {Match}: {Error}", entries[i].GetValue("matchId", BsonNull.Value), e.Message);
            }
        }

        log.LogInformation("/matches/all/{Account}: returning {Count} matches from PlayerStats", accountId, matches.Count);
        double pages = Math.Ceiling(entries.Count / count);
        return new JsonObject
        {
            ["matches"] = matches,
            ["total_matches"] = entries.Count,
            ["current_page"] = page,
            // Math.ceil(total / count) || 1: no entries (0) is one page.
            ["total_pages"] = pages == 0 || double.IsNaN(pages) ? 1 : pages,
        };
    }

    // stats?.[field] || []: a missing or empty-ish value is no entries; anything else that is not an array fails the
    // request (in JavaScript, spreading it throws).
    private static IEnumerable<BsonValue> List(BsonDocument? stats, string field) =>
        stats?.GetValue(field, BsonNull.Value) switch
        {
            null or BsonNull or BsonUndefined => [],
            BsonArray array => array,
            { IsBoolean: true } b when !b.AsBoolean => [],
            { IsNumeric: true } n when n.ToDouble() == 0 || double.IsNaN(n.ToDouble()) => [],
            BsonString { Value: "" } => [],
            var other => throw new FormatException($"{field} is a {other.BsonType}"),
        };

    // (a.timestamp || 0) - (b.timestamp || 0); a timestamp that is not a number sorts as 0 (its match is left out).
    private static double SortKey(BsonValue timestamp) =>
        timestamp is { IsNumeric: true } n && !double.IsNaN(n.ToDouble()) ? n.ToDouble() : 0;

    /// <summary>One entry as a completed match document (buildMatchResponse); its keys in the TS server's order.</summary>
    private static JsonObject Match(string accountId, BsonDocument entry, IReadOnlyDictionary<string, string> names, DateTimeOffset now, Random random)
    {
        var mode = entry.GetValue("mode", BsonNull.Value);
        string modeString = mode switch
        {
            BsonString s => s.Value,
            BsonNull or BsonUndefined => "",
            _ => throw new FormatException($"a {mode.BsonType} mode"),
        };
        bool is1v1 = modeString.Contains("1v1", StringComparison.Ordinal);
        bool ffa = modeString.Equals("FFA", StringComparison.OrdinalIgnoreCase);
        string templateSlug = is1v1 ? "1v1_container" : ffa ? "ffa_container" : "2v2_container";
        if (modeString.Length == 0)
        {
            modeString = is1v1 ? "1v1" : "2v2";
        }
        bool didWin = entry.GetValue("result", BsonNull.Value) is BsonString { Value: "win" };
        var score = entry.GetValue("score", BsonNull.Value) switch
        {
            BsonArray a => a,
            BsonNull or BsonUndefined => [],
            var other => throw new FormatException($"a {other.BsonType} score"),
        };
        // entry.score?.[0] ?? 0: the game's own Score, by team (the same for every player).
        JsonNode? team0Score = score.Count > 0 && score[0] is not (BsonNull or BsonUndefined) ? Value(score[0]) : 0;
        JsonNode? team1Score = score.Count > 1 && score[1] is not (BsonNull or BsonUndefined) ? Value(score[1]) : 0;

        // entry.players || []
        var players = (entry.GetValue("players", BsonNull.Value) switch
        {
            BsonArray a => a,
            BsonNull or BsonUndefined => [],
            var other => throw new FormatException($"a {other.BsonType} players list"),
        }).Select(p => p as BsonDocument ?? throw new FormatException($"a {p.BsonType} player")).ToList();

        var matchIdValue = entry.GetValue("matchId", BsonNull.Value);
        JsonNode? id = Truthy(matchIdValue) ? Value(matchIdValue) : ObjectId.GenerateNewId().ToString();
        var opponent = players.FirstOrDefault(p => !IsAccount(p, accountId));
        var opponentIdValue = opponent?.GetValue("accountId", BsonNull.Value);
        JsonNode? opponentId = opponentIdValue is not null && Truthy(opponentIdValue)
            ? Value(opponentIdValue)
            : $"opp_{(Truthy(matchIdValue) ? JsString(matchIdValue) : "unknown")}";

        // new Date(entry.timestamp || Date.now()); a date past JavaScript's range (or .NET's) leaves the match out.
        var timestamp = entry.GetValue("timestamp", BsonNull.Value);
        double ms = !Truthy(timestamp) ? now.ToUnixTimeMilliseconds()
            : timestamp.IsNumeric ? timestamp.ToDouble()
            : throw new FormatException($"a {timestamp.BsonType} timestamp");
        var createdAt = DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Truncate(ms));
        string ts = createdAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        long seconds = (long)Math.Floor(createdAt.ToUnixTimeMilliseconds() / 1000.0);

        // The winners are the players the stats writer marked isWinner (all on one team); the team is theirs.
        var winnerFlags = players.Select(p => p.GetValue("isWinner", BsonNull.Value) is BsonBoolean { Value: true }).ToList();
        JsonArray winners, losers;
        JsonNode? winningTeam;
        if (winnerFlags.Contains(true))
        {
            winners = new JsonArray(players.Where((_, i) => winnerFlags[i]).Select(p => Field(p, "accountId")).ToArray());
            losers = new JsonArray(players.Where((_, i) => !winnerFlags[i]).Select(p => Field(p, "accountId")).ToArray());
            winningTeam = Field(players[winnerFlags.IndexOf(true)], "teamIndex");
        }
        else
        {
            winners = new JsonArray(didWin ? (JsonNode?)accountId : opponentId?.DeepClone());
            losers = new JsonArray(didWin ? opponentId?.DeepClone() : (JsonNode?)accountId);
            winningTeam = didWin ? 0 : 1;
        }

        // Every player's stats, shared by every player's EndOfMatchStats.
        var missionUpdates = new JsonObject();
        foreach (var p in players)
        {
            missionUpdates[Key(p)] = new JsonObject
            {
                ["Stat:Game:Character:TotalDamageDealt"] = Field(p, "damage"),
                ["Stat:Game:Character:TotalAttackDamageDealt"] = Field(p, "damage"),
                ["Stat:Game:Character:TotalRingouts"] = Field(p, "ringouts"),
                ["Stat:Game:Character:TotalDeaths"] = Field(p, "deaths"),
            };
        }

        var all = new JsonArray();
        foreach (var p in players)
        {
            all.Add(new JsonObject
            {
                ["account_id"] = Field(p, "accountId"),
                ["data"] = new JsonObject
                {
                    ["EndOfMatchStats"] = new JsonObject
                    {
                        ["Score"] = new JsonArray(team0Score?.DeepClone(), team1Score?.DeepClone()),
                        ["WinningTeamIndex"] = winningTeam?.DeepClone(),
                        ["PlayerMissionUpdates"] = missionUpdates.DeepClone(),
                    },
                },
                ["identity"] = new JsonObject
                {
                    ["username"] = names.GetValueOrDefault(Key(p), ""),
                    ["avatar"] = "",
                    ["default_username"] = false,
                    ["personal_data"] = new JsonObject(),
                    ["alternate"] = new JsonObject(),
                    ["usernames"] = new JsonArray(new JsonObject { ["auth"] = "steam", ["username"] = names.GetValueOrDefault(Key(p), "") }),
                    ["platforms"] = new JsonArray("steam"),
                    ["current_platform"] = "steam",
                    ["is_cross_platform"] = false,
                },
                ["source"] = new JsonObject(),
                ["state"] = "completed",
                ["state_data"] = null,
            });
        }

        // Object.fromEntries: a repeated account keeps its first place and its last value.
        var gameplayPlayers = new JsonObject();
        for (int i = 0; i < players.Count; i++)
        {
            var p = players[i];
            string character = p.GetValue("character", BsonNull.Value) is BsonString s ? s.Value : throw new FormatException("a player with no character");
            int prefix = character.IndexOf("character_", StringComparison.Ordinal);
            gameplayPlayers[Key(p)] = new JsonObject
            {
                ["AccountId"] = Field(p, "accountId"),
                ["Character"] = character,
                // `skin_${character.replace("character_", "")}_default`: the first occurrence only.
                ["Skin"] = $"skin_{(prefix < 0 ? character : character.Remove(prefix, "character_".Length))}_default",
                ["TeamIndex"] = Field(p, "teamIndex"),
                ["PlayerIndex"] = i,
                ["Banner"] = "",
                ["ProfileIcon"] = "",
                ["RingoutVfx"] = "",
                ["Perks"] = new JsonArray(),
                ["Taunts"] = new JsonArray(),
                ["Gems"] = new JsonArray(),
                ["Buffs"] = new JsonArray(),
                ["StatTrackers"] = new JsonArray(),
                ["BotBehaviorOverride"] = "",
                ["BotDifficultyMin"] = 0,
                ["BotDifficultyMax"] = 0,
                ["Handicap"] = 0,
                ["StartingDamage"] = 0,
                ["GameplayPreferences"] = 0,
                ["CrossplayPreference"] = 0,
                ["PartyId"] = "",
                ["PartyMember"] = null,
                ["LobbyPlayerIndex"] = i,
                ["RankedDivision"] = null,
                ["RankedTier"] = null,
                ["WinStreak"] = null,
                ["bAutoPartyPreference"] = false,
                ["bIsBot"] = false,
                ["bUseCharacterDisplayName"] = false,
            };
        }

        return new JsonObject
        {
            ["access"] = "public",
            ["access_level"] = "open",
            ["account_id"] = null,
            ["arbitration"] = new JsonObject { ["conflict_resolved"] = false, ["end_time"] = ts, ["start_time"] = ts },
            ["cluster"] = "ec2-us-east-1-dokken",
            ["completion_time"] = new JsonObject { ["_hydra_unix_date"] = seconds },
            ["created_at"] = new JsonObject { ["_hydra_unix_date"] = seconds },
            ["criteria"] = new JsonObject { ["slug"] = templateSlug },
            ["draw"] = false,
            ["id"] = id,
            ["last_warning_time"] = null,
            ["loss"] = losers,
            ["matchmaking"] = null,
            ["name"] = templateSlug,
            ["origin"] = "matchmaking",
            ["players"] = new JsonObject
            {
                ["all"] = all,
                ["completed"] = new JsonArray(players.Select(p => Field(p, "accountId")).ToArray()),
                // opts.playerStats?.length || 2
                ["count"] = players.Count == 0 ? 2 : players.Count,
                ["current"] = new JsonArray(),
            },
            ["rand"] = random.NextDouble(),
            ["server_data"] = new JsonObject
            {
                ["GameplayConfig"] = new JsonObject
                {
                    ["Map"] = entry.GetValue("map", BsonNull.Value) is var map && Truthy(map) ? Value(map) : "",
                    ["ModeString"] = modeString,
                    ["MatchId"] = id?.DeepClone(),
                    ["bIsPvP"] = true,
                    ["bIsRanked"] = false,
                    ["bIsOnlineMatch"] = true,
                    ["bIsCustomGame"] = false,
                    ["bIsCasualSpecial"] = false,
                    ["bIsRift"] = false,
                    ["bIsTutorial"] = false,
                    ["bModeGrantsProgress"] = true,
                    ["bAllowMapHazards"] = true,
                    ["MatchDurationSeconds"] = 300,
                    ["ScoreEvaluationRule"] = "BestOf5",
                    ["ScoreAttributionRule"] = "RingoutAndTimer",
                    ["CountdownDisplay"] = "StocksRemaining",
                    ["Cluster"] = "ec2-us-east-1-dokken",
                    ["Created"] = ts,
                    ["EventQueueSlug"] = "",
                    ["RiftNodeId"] = "",
                    ["RiftNodeAttunement"] = "",
                    ["HudSettings"] = new JsonObject { ["bDisplayPortraits"] = true, ["bDisplayStocks"] = true, ["bDisplayTimer"] = true },
                    ["CustomGameSettings"] = new JsonObject { ["NumRingouts"] = 3, ["MatchTime"] = 300, ["bHazardsEnabled"] = true, ["bShieldsEnabled"] = true },
                    ["Players"] = gameplayPlayers,
                    ["Spectators"] = new JsonObject(),
                    ["TeamData"] = new JsonArray(),
                    ["WorldBuffs"] = new JsonArray(),
                },
                ["bGameplayStarted"] = true,
                ["bGameplayEnded"] = true,
            },
            ["shortcode"] = null,
            ["state"] = "completed",
            ["template"] = new JsonObject
            {
                ["type"] = "match",
                ["name"] = templateSlug,
                ["slug"] = templateSlug,
                ["min_players"] = 2,
                ["max_players"] = is1v1 ? 2 : 4,
                ["game_server_integration_enabled"] = true,
                ["game_server_config"] = null,
                ["data"] = new JsonObject { ["mode"] = modeString },
                ["created_at"] = new JsonObject { ["_hydra_unix_date"] = 1658510618 },
                ["updated_at"] = new JsonObject { ["_hydra_unix_date"] = 1658510618 },
                ["id"] = ObjectId.GenerateNewId().ToString(),
            },
            ["updated_at"] = new JsonObject { ["_hydra_unix_date"] = seconds },
            ["win"] = winners,
            ["winning_team"] = new JsonArray(winningTeam?.DeepClone()),
        };
    }

    private static bool IsAccount(BsonDocument player, string accountId) =>
        player.GetValue("accountId", BsonNull.Value) is BsonString { Value: var id } && id == accountId;

    // A player's key in the per-player maps: its account id (JavaScript would turn anything into a string).
    private static string Key(BsonDocument player) =>
        player.GetValue("accountId", BsonNull.Value) is BsonString s ? s.Value : throw new FormatException("a player whose accountId is not a string");

    // A player's field; the stats writer always stores them (undefined has no Hydra encoding to match).
    private static JsonNode? Field(BsonDocument player, string name) =>
        player.TryGetValue(name, out var value) ? Value(value) : throw new FormatException($"a player with no {name}");

    /// <summary>A stored value as the TS server's lean read hands it on: strings, numbers, booleans, null, arrays, documents.</summary>
    private static JsonNode? Value(BsonValue value) => value switch
    {
        BsonNull => null,
        BsonString s => s.Value,
        BsonBoolean b => b.Value,
        BsonInt32 i => i.Value,
        BsonInt64 l => l.Value,
        BsonDouble d => d.Value,
        BsonArray a => new JsonArray(a.Select(Value).ToArray()),
        BsonDocument doc => new JsonObject(doc.Select(e => KeyValuePair.Create(e.Name, Value(e.Value)))),
        _ => throw new FormatException($"a {value.BsonType} value"),
    };

    private static bool Truthy(BsonValue value) => value switch
    {
        BsonNull or BsonUndefined => false,
        BsonString s => s.Value.Length > 0,
        BsonBoolean b => b.Value,
        { IsNumeric: true } n => n.ToDouble() is var d && d != 0 && !double.IsNaN(d),
        _ => true,
    };

    // `${value}` for the values a matchId can be.
    private static string JsString(BsonValue value) => value switch
    {
        BsonString s => s.Value,
        _ => Js.Stringify(Value(value)),
    };
}

public static class MatchHistoryHosting
{
    public static WebApplicationBuilder AddMatchHistory(this WebApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IMatchHistoryService, MatchHistoryService>();
        return builder;
    }
}

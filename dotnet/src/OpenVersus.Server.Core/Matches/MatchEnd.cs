using System.ComponentModel;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Leaderboards;
using OpenVersus.Server.Core.Realtime;
using OpenVersus.Server.Core.Seasons;
using OpenVersus.Server.Core.Settings;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Matches;

// A match's end, once every player has left its rollback server (/ovs_end_match, RollbackCallbacks), ported from the TS
// websocket's handleOnMatchEnd (websocket.ts, branch infinity-war; the TS websocket runs it for every match:end):
//
//   1. Each player's game gets EndOfMatchPayload, the config it played (match_config:{player}, GameplayConfigs), which is
//      then dropped; a player online is set idle (player:{id} status).
//   2. A custom lobby's match (ssc_custom_lobby_match:{match}): its rematch vote opens (Rematches), and nothing more
//      happens here (no set, no party preservation), even after a crash, as there. The lobby gone: nothing at all.
//   3. The set (RankedSets.GameEndedAsync): a match that crashed sends everyone back to the menus (an empty config,
//      +500 ms); a set game with no set (orphan) or a set already resolved: MatchSetLeaverNotification (+1000 ms) and the
//      empty config (+1500 ms); a set that goes on: nothing (the game shows its check-in); a set over: each rated
//      player's FullRankUpdate at once, then the leaver notification and the empty config to every player of the set.
//      Not a set game: a Casual game (isCustomGame) gets its rematch vote (Rematches); one with no config
//      RematchDeclinedNotification (+1000 ms); any other (a rift) nothing more (decided 2026-10-05: what the game saw
//      before).
//   4. Unless a player is still in a set: party preservation. A player whose party lobby has others in it keeps it (lobby
//      and player_lobby saved again, 8 h; its ready set cleared) and is marked rejoin_pending:{player} (45 s), the window
//      in which their reconnecting must not cost them the party. A player whose game closes in the window is taken out
//      of their lobbies when it closes, unless they came back (Realtime/LobbyDisconnects.cs).
// A crash, an orphan or a resolved set ends there (no party preservation), as there.
// Delayed messages go through DelayedMessages (Redis, swept every 100 ms), never an in-process timer.
//
// Redis, read     match_end:{match}; match_config:{player}; online_players; ssc_custom_lobby_match:{match}; {match};
//                 match:{match}; player_ranked_set:{player}; player_lobby:{player}; lobby:{lobby} (and RankedSets' and
//                 the rematch vote's: RematchVotes)
// Redis, written  match_end:{match} NX EX 10 min (once per match); match_config:{player} deleted; player:{player} status;
//                 lobby:{lobby}, player_lobby:{player} EX 8 h; party_ready:{lobby} deleted; rejoin_pending:{player} EX 45 s;
//                 realtime:due (and RankedSets' and the rematch vote's)
// Published       ws:send (each message, to its player)
// Mongo           FullRankUpdate's reads (eloratings made when missing), and RankedSets' ratings
//
// Unlike there:
//   a Casual game has a rematch (Rematches); TS declined it for everyone a second after its end (MIGRATION-BRIDGES.md 7).
//   handled once per match (match_end:{match}): a second /ovs_end_match (a relay and a node, a retry) changes nothing; TS
//     counted the game again for every publish (and each websocket replica would have).
//   the echo is the config kept for this match only (TS echoed whatever its connection held, a newer match's included).
//   a set game is one RatedMatches counts, a dodge flag counts only for the set it names, and the set is updated under its
//     lock (RankedSets).
//   FullRankUpdate is for Season:Current (TS: Season:SeasonFive).
//   party preservation is skipped when any player is still in a set (TS asked Promise.any, which settled with the first
//     player's answer).
//   the web custom lobby's end (custom_lobby_match:{match}) is not ported: its pages were retired (docs/REALTIME.md).

public sealed class MatchEndSettings
{
    [Description("The match flow ends matches itself (MatchEnd) instead of publishing match:end for the TS websocket. Off until the realtime gateway replaces the TS websocket, which keeps each player's match config in its own memory (docs/MIGRATION-BRIDGES.md 2); on only where no TS websocket holds the players (the parity harness). Realtime:Gateway on ends them here whatever this says.")]
    public bool Enabled { get; set; }
}

public interface IMatchEnd
{
    /// <summary>The end of <paramref name="matchId"/>, played by <paramref name="playerIds"/> (its config's players, in order).</summary>
    Task EndAsync(string matchId, IReadOnlyList<string> playerIds, CancellationToken ct = default);
}

internal sealed class MatchEnd(IServiceProvider services, IRankedSets sets, EloRatings ratings, IOptionsMonitor<SeasonSettings> season,
    TimeProvider time, ILogger<MatchEnd> log) : IMatchEnd
{
    private static readonly TimeSpan s_onceTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan s_lobbyTtl = TimeSpan.FromHours(8);
    private static readonly TimeSpan s_rejoinWindow = TimeSpan.FromSeconds(45);

    public async Task EndAsync(string matchId, IReadOnlyList<string> playerIds, CancellationToken ct)
    {
        var redis = services.GetService<IConnectionMultiplexer>()?.GetDatabase() ?? throw new InvalidOperationException("this service has no Redis (REDIS)");
        if (!await redis.StringSetAsync($"match_end:{matchId}", "1", s_onceTtl, When.NotExists))
        {
            log.LogInformation("Match {Match} already ended; ignoring", matchId);
            return;
        }

        foreach (string id in playerIds)
        {
            await EchoAsync(redis, matchId, id);
        }

        if ((string?)await redis.StringGetAsync($"ssc_custom_lobby_match:{matchId}") is { Length: > 0 } lobby)
        {
            if (await RematchVotes.OpenLobbyAsync(redis, time, matchId, lobby))
            {
                log.LogInformation("Match {Match} ended in custom lobby {Lobby}: rematch vote open for {Seconds} s", matchId, lobby, RematchVotes.Timer.TotalSeconds);
            }
            else
            {
                log.LogWarning("Match {Match} ended in custom lobby {Lobby}, which is gone", matchId, lobby);
            }

            return;
        }

        var config = await RollbackCallbacks.JsonAsync(redis, matchId);
        bool counts = config is not null && RatedMatches.WhyNotRated(Str(config["mode"]), config["players"] as JsonArray,
            await RollbackCallbacks.JsonAsync(redis, $"match:{matchId}"), config) is null;
        var end = await sets.GameEndedAsync(matchId, playerIds, config, counts);
        switch (end.Kind)
        {
            case GameEnd.Crashed:
                await IdleAsync(redis, playerIds);
                foreach (string id in playerIds)
                {
                    await DelayedMessages.ScheduleAsync(redis, time, [id], EmptyConfig(), TimeSpan.FromMilliseconds(500));
                }

                return;
            case GameEnd.Orphan or GameEnd.Resolved:
                await IdleAsync(redis, playerIds);
                foreach (string id in playerIds)
                {
                    await DelayedMessages.ScheduleAsync(redis, time, [id], Leaver(id, matchId, toMatch: true), TimeSpan.FromSeconds(1));
                    await DelayedMessages.ScheduleAsync(redis, time, [id], EmptyConfig(), TimeSpan.FromMilliseconds(1500));
                }

                return;
            case GameEnd.Over:
                await FullRankUpdatesAsync(redis, end.RatedPlayerIds, ct);
                foreach (string id in end.SetPlayerIds)
                {
                    await DelayedMessages.ScheduleAsync(redis, time, [id], Leaver(id, matchId, toMatch: false), TimeSpan.FromSeconds(1));
                    await DelayedMessages.ScheduleAsync(redis, time, [id], EmptyConfig(), TimeSpan.FromMilliseconds(1500));
                }

                break;
            case GameEnd.NotASet when config is not null && RollbackCallbacks.Truthy(config["isCustomGame"]):
                await RematchVotes.OpenCasualAsync(redis, time, matchId, config);
                log.LogInformation("Casual match {Match} ended: rematch vote open for {Seconds} s", matchId, RematchVotes.Timer.TotalSeconds);
                break;
            case GameEnd.NotASet when config is null:
                foreach (string id in playerIds)
                {
                    await DelayedMessages.ScheduleAsync(redis, time, [id], RematchDeclined(id, matchId), TimeSpan.FromSeconds(1));
                }

                break;
        }

        foreach (string id in playerIds)
        {
            if ((await redis.StringGetAsync($"player_ranked_set:{id}")).HasValue)
            {
                log.LogInformation("Match {Match}: skipping party preservation — players are in an active ranked set", matchId);
                return;
            }
        }

        foreach (string id in playerIds)
        {
            await PreservePartyAsync(redis, id);
        }
    }

    // EndOfMatchPayload with the config the player played, which is then dropped; idle when online.
    private async Task EchoAsync(IDatabase redis, string matchId, string playerId)
    {
        if (await RollbackCallbacks.JsonAsync(redis, GameplayConfigs.Key(playerId)) is not { } kept)
        {
            log.LogWarning("Ignoring the end of {Match} for {Player} — no match config kept", matchId, playerId);
            return;
        }

        if (Str(kept["payload"]?["match"]?["id"]) != matchId)
        {
            log.LogWarning("Ignoring the end of {Match} for {Player} — their config is match {Other}'s", matchId, playerId, Str(kept["payload"]?["match"]?["id"]));
            return;
        }

        await PlayerMessages.SendAsync(redis, [playerId], new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["GameplayConfig"] = kept["data"]?["GameplayConfig"]?.DeepClone(),
                ["template_id"] = "EndOfMatchPayload",
                ["ClientReturnData"] = new JsonObject(),
            },
            ["payload"] = Frm(playerId),
            ["header"] = "",
            ["cmd"] = "profile-notification",
        });
        await redis.KeyDeleteAsync(GameplayConfigs.Key(playerId));
        await IdleAsync(redis, [playerId]);
    }

    // player:{id} status idle for the players online (TS: the ones it held a connection for).
    private static async Task IdleAsync(IDatabase redis, IEnumerable<string> playerIds)
    {
        foreach (string id in playerIds)
        {
            if (await redis.SetContainsAsync("online_players", id))
            {
                await redis.HashSetAsync($"player:{id}", "status", "idle");
            }
        }
    }

    private async Task FullRankUpdatesAsync(IDatabase redis, IReadOnlyList<string> playerIds, CancellationToken ct)
    {
        if (services.GetService<IMongoDatabase>() is not { } mongo)
        {
            log.LogError("No FullRankUpdate: this service has no Mongo (MONGODB_URI)");
            return;
        }

        foreach (string id in playerIds)
        {
            try
            {
                await PlayerMessages.SendAsync(redis, [id], await FullRankUpdate.BuildAsync(redis, mongo, ratings, id, season.CurrentValue.Current, time, ct));
            }
            catch (Exception e) when (e is MongoException or RedisException or TimeoutException)
            {
                log.LogError("Error sending FullRankUpdate to {Player}: {Error}", id, e.Message);
            }
        }
    }

    // A player whose party has others in it keeps it through the end of the match (see the header).
    private async Task PreservePartyAsync(IDatabase redis, string playerId)
    {
        if ((string?)await redis.StringGetAsync($"player_lobby:{playerId}") is not { Length: > 0 } lobbyId
            || await RollbackCallbacks.JsonAsync(redis, $"lobby:{lobbyId}") is not { } lobby
            || (lobby["playerIds"] as JsonArray)?.Count is not > 1)
        {
            return;
        }

        await redis.StringSetAsync($"lobby:{lobbyId}", Js.Stringify(lobby), s_lobbyTtl);
        await redis.StringSetAsync($"player_lobby:{playerId}", lobbyId, s_lobbyTtl);
        await redis.KeyDeleteAsync($"party_ready:{lobbyId}");
        await redis.StringSetAsync($"rejoin_pending:{playerId}", "1", s_rejoinWindow);
        log.LogInformation("Post-match: player {Player} keeps party lobby {Lobby} ({Count} players)", playerId, lobbyId, (lobby["playerIds"] as JsonArray)!.Count);
    }

    private static JsonObject Frm(string playerId) => new()
    {
        ["frm"] = new JsonObject { ["id"] = "internal-server", ["type"] = "server-api-key" },
        ["template"] = "realtime",
        ["account_id"] = playerId,
        ["profile_id"] = playerId,
    };

    // The empty config: the game goes back to its menus.
    internal static JsonObject EmptyConfig() => new()
    {
        ["data"] = new JsonObject { ["MatchId"] = "", ["GameplayConfig"] = null, ["template_id"] = "OnGameplayConfigNotified" },
        ["payload"] = new JsonObject { ["match"] = new JsonObject { ["id"] = "" }, ["custom_notification"] = "realtime" },
        ["header"] = "",
        ["cmd"] = "update",
    };

    // MatchSetLeaverNotification; an orphan's or a resolved set's payload names the match, a set over's comes from the server.
    private static JsonObject Leaver(string playerId, string matchId, bool toMatch) => new()
    {
        ["data"] = new JsonObject { ["AccountId"] = playerId, ["MatchId"] = matchId, ["template_id"] = "MatchSetLeaverNotification" },
        ["payload"] = toMatch
            ? new JsonObject
            {
                ["match"] = new JsonObject { ["id"] = matchId },
                ["template"] = "realtime",
                ["account_id"] = playerId,
                ["profile_id"] = playerId,
                ["custom_notification"] = "realtime",
            }
            : Frm(playerId),
        ["header"] = "",
        ["cmd"] = "profile-notification",
    };

    private static JsonObject RematchDeclined(string playerId, string matchId) => new()
    {
        ["data"] = new JsonObject { ["AccountId"] = playerId, ["MatchId"] = matchId, ["template_id"] = "RematchDeclinedNotification" },
        ["payload"] = Frm(playerId),
        ["header"] = "",
        ["cmd"] = "profile-notification",
    };

    private static string? Str(JsonNode? value) => value is JsonValue v && v.TryGetValue(out string? s) ? s : null;
}

public static class MatchEndHosting
{
    /// <summary>The match's end (MatchEnd:Enabled; RollbackCallbacks calls it), and the sweep that sends its delayed messages.</summary>
    public static WebApplicationBuilder AddMatchEnd(this WebApplicationBuilder builder)
    {
        builder.AddSetting<MatchEndSettings>("MatchEnd");
        builder.AddSetting<SeasonSettings>("Season");
        builder.AddEloRatings();
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.TryAddSingleton<IMatchEnd, MatchEnd>();
        builder.Services.AddHostedService<DelayedMessageSweep>();
        return builder;
    }
}

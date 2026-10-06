using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenVersus.Server.Core.Clients;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.CustomLobbies;
using OpenVersus.Server.Core.Matchmaking;
using OpenVersus.Server.Core.Realtime;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Matches;

// The rematch after a single game: a custom lobby's match, ported from the TS server's (modules/customLobby
// lobby.service.ts handleSscCustomLobbyMatchEnd, handleSscRematchAccept/Decline, triggerSscRematch; the routes in
// modules/lobby/shared.routes.ts), and a Casual match's, which the TS server never had (its websocket declined every
// Casual rematch: MIGRATION-BRIDGES.md 7). Ranked games have no rematch: a set goes on through its check-ins.
//
//   Open (MatchEnd, in the match flow): the vote and its timer. A custom lobby's: every ready flag taken down (a script),
//     ssc_custom_lobby_match:{match} deleted, ssc_custom_lobby_rematch_timer:{lobby} = the match. A Casual match's:
//     casual_rematch:{match} = the match's config (what the rematch is made from), casual_rematch_player:{player} for
//     each of its players (not bots). Either way an entry on rematch:due, 25 s on (the game's RematchTimeoutSeconds).
//   rematch_accept (lobbies): the player's accept is added (..._accept, a set). When every player who votes has
//     accepted, the rematch starts. Who votes: a lobby's players (not bots) in teams 0-3, as the lobby is now; a Casual
//     match's players (not bots). Bots and spectators have no say.
//   The timer (RematchSweep, in lobbies): a vote still open when its 25 s are up starts the rematch, accepted or not:
//     on the game's post-match screen the timer running out is a rematch.
//   rematch_decline (lobbies): the vote is over, and everyone (a lobby's players, spectators too; a Casual match's
//     players) is sent RematchDeclinedNotification, which takes the game back to its menus.
//   The start: a lobby's match as its leader's start_custom_match (ICustomLobbyService.RematchAsync); a Casual match
//     again with the same players, teams, player indexes, host and parties, the bots the same fighters (bot_config, a
//     day), on a new map, unranked as before (IMatchLauncher). A rematch that cannot start (no player but bots left in
//     the lobby's teams, a client that must update, no rollback port) is declined for everyone.
//
// The vote's timer key is its claim: the accept or the timer that deletes it (a script comparing it with the match, for
// a lobby: GETDEL, for a Casual match) is the one that starts the rematch, so it starts once, whichever replica of
// lobbies gets there. A decline deletes it too.
//
// Redis, read     ssc_custom_lobby_player:{player}; custom_lobby_ssc:{lobby}; casual_rematch_player:{player} (and the
//                 lobby's and the launcher's)
// Redis, written  ssc_custom_lobby_rematch_timer:{lobby} = match EX 30; ssc_custom_lobby_rematch_accept:{lobby} set EX 30;
//                 ssc_custom_lobby_match:{match} deleted; casual_rematch:{match} = config EX 30;
//                 casual_rematch_accept:{match} set EX 30; casual_rematch_player:{player} = match EX 30;
//                 rematch:due (ZADD; ZREM by the sweep)
// Published       ws:send (RematchDeclinedNotification)
//
// Unlike the TS server's (each asserted by tools/matches/match_end_diff.mjs):
//   two accepts at once, or an accept and the timer, start one rematch, not one each (TS: GET, SADD, SCARD, DEL, start,
//     with nothing between them).
//   a spectator's accept does not count for a player's (TS counted every accept against the players' number).
//   a rematch with no player left to play it is not started (TS started it, and the game waited for it forever).
//   the timer is in Redis (rematch:due), not in the process that ended the match.
//   the decline is sent to the players through ws:send (TS published custom_lobby_rematch_decline for its websocket).
// The web custom lobby's rematch (custom_lobby_player:{player}, the TS routes' fallback) is not ported: its pages were
// retired (docs/REALTIME.md).
// Who is still there is not checked for a Casual rematch: that is the realtime gateway's to know; until then a player
// who left without declining is in the rematch (the TS lobby rematch is the same).

public static class RematchVotes
{
    public const string DueKey = "rematch:due";

    /// <summary>How long a vote is open (the game's RematchTimeoutSeconds).</summary>
    public static readonly TimeSpan Timer = TimeSpan.FromSeconds(25);

    // The vote's keys outlive the timer (as the TS server's EX 30), so the timer still finds them.
    private static readonly TimeSpan s_ttl = TimeSpan.FromSeconds(30);

    public static string LobbyTimerKey(string lobbyId) => $"ssc_custom_lobby_rematch_timer:{lobbyId}";
    public static string LobbyAcceptKey(string lobbyId) => $"ssc_custom_lobby_rematch_accept:{lobbyId}";
    public static string LobbyPlayerKey(string playerId) => $"ssc_custom_lobby_player:{playerId}";
    public static string CasualKey(string matchId) => $"casual_rematch:{matchId}";
    public static string CasualAcceptKey(string matchId) => $"casual_rematch_accept:{matchId}";
    public static string CasualPlayerKey(string playerId) => $"casual_rematch_player:{playerId}";

    /// <summary>A custom lobby's match ended: its vote opened. False when the lobby is gone (then nothing is done).</summary>
    public static async Task<bool> OpenLobbyAsync(IDatabase redis, TimeProvider time, string matchId, string lobbyId)
    {
        if (!await CustomLobbyService.MatchEndedAsync(redis, lobbyId))
        {
            return false;
        }

        await redis.KeyDeleteAsync($"ssc_custom_lobby_match:{matchId}");
        await redis.StringSetAsync(LobbyTimerKey(lobbyId), matchId, s_ttl);
        await redis.SortedSetAddAsync(DueKey, $"lobby:{lobbyId}:{matchId}", Due(time));
        return true;
    }

    /// <summary>A Casual match ended: its vote opened, holding the match's config (<paramref name="config"/>).</summary>
    public static async Task OpenCasualAsync(IDatabase redis, TimeProvider time, string matchId, JsonObject config)
    {
        await redis.StringSetAsync(CasualKey(matchId), Js.Stringify(config), s_ttl);
        foreach (string id in Voters(config))
        {
            await redis.StringSetAsync(CasualPlayerKey(id), matchId, s_ttl);
        }

        await redis.SortedSetAddAsync(DueKey, $"casual:{matchId}", Due(time));
    }

    internal static async Task AcceptedAsync(IDatabase redis, string acceptKey, string playerId)
    {
        await redis.SetAddAsync(acceptKey, playerId);
        await redis.KeyExpireAsync(acceptKey, s_ttl);
    }

    /// <summary>A Casual match's players who vote: its players, not bots or spectators, in the config's order.</summary>
    internal static List<string> Voters(JsonObject config) =>
        [.. (config["players"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(p => !RollbackCallbacks.Truthy(p["isBot"]) && !RollbackCallbacks.Truthy(p["isSpectator"]))
            .Select(p => Str(p["playerId"]) ?? "").Where(id => id.Length > 0)];

    private static double Due(TimeProvider time) => (time.GetUtcNow() + Timer).ToUnixTimeMilliseconds();

    internal static string? Str(JsonNode? value) => value is JsonValue v && v.TryGetValue(out string? s) ? s : null;
}

public interface IRematches
{
    /// <summary>rematch_accept from <paramref name="playerId"/>: their accept counted, and the rematch started when it was the last one.</summary>
    Task AcceptAsync(string playerId, CancellationToken ct = default);

    /// <summary>rematch_decline from <paramref name="playerId"/>: the vote over, everyone told.</summary>
    Task DeclineAsync(string playerId, CancellationToken ct = default);

    /// <summary>A vote's 25 s are up (<paramref name="entry"/>, from rematch:due): the rematch started if the vote is still open.</summary>
    Task TimerAsync(string entry, CancellationToken ct = default);
}

internal sealed class Rematches(IServiceProvider services, ICustomLobbyService lobbies, IMatchLauncher launcher, IClientUpdateGate gate,
    ILogger<Rematches> log) : IRematches
{
    // The lobby's timer, deleted only while it still names the match (a newer vote's is left alone): 1 when deleted.
    private const string ClaimScript = "if redis.call('GET', KEYS[1]) == ARGV[1] then return redis.call('DEL', KEYS[1]) end return 0";

    public async Task AcceptAsync(string playerId, CancellationToken ct)
    {
        var redis = Redis();
        if ((string?)await redis.StringGetAsync(RematchVotes.LobbyPlayerKey(playerId)) is { Length: > 0 } lobbyId
            && await CustomLobbyService.HumansAsync(redis, lobbyId) is { } humans)
        {
            if ((string?)await redis.StringGetAsync(RematchVotes.LobbyTimerKey(lobbyId)) is not { Length: > 0 } matchId)
            {
                return;
            }

            await RematchVotes.AcceptedAsync(redis, RematchVotes.LobbyAcceptKey(lobbyId), playerId);
            log.LogInformation("Player {Player} accepted the rematch in custom lobby {Lobby}", playerId, lobbyId);
            if (humans.Playing.Count > 0 && await AllAcceptedAsync(redis, RematchVotes.LobbyAcceptKey(lobbyId), humans.Playing)
                && await ClaimLobbyAsync(redis, lobbyId, matchId))
            {
                log.LogInformation("All {Count} players accepted the rematch in custom lobby {Lobby}", humans.Playing.Count, lobbyId);
                await StartLobbyAsync(redis, lobbyId, ct);
            }

            return;
        }

        if ((string?)await redis.StringGetAsync(RematchVotes.CasualPlayerKey(playerId)) is not { Length: > 0 } match
            || await RollbackCallbacks.JsonAsync(redis, RematchVotes.CasualKey(match)) is not { } config)
        {
            log.LogInformation("rematch_accept from {Player}: no rematch to accept", playerId);
            return;
        }

        await RematchVotes.AcceptedAsync(redis, RematchVotes.CasualAcceptKey(match), playerId);
        log.LogInformation("Player {Player} accepted the rematch of Casual match {Match}", playerId, match);
        if (await AllAcceptedAsync(redis, RematchVotes.CasualAcceptKey(match), RematchVotes.Voters(config))
            && await ClaimCasualAsync(redis, match) is { } claimed)
        {
            log.LogInformation("Every player accepted the rematch of Casual match {Match}", match);
            await StartCasualAsync(redis, match, claimed, ct);
        }
    }

    public async Task DeclineAsync(string playerId, CancellationToken ct)
    {
        var redis = Redis();
        if ((string?)await redis.StringGetAsync(RematchVotes.LobbyPlayerKey(playerId)) is { Length: > 0 } lobbyId
            && await CustomLobbyService.HumansAsync(redis, lobbyId) is { } humans)
        {
            // As the TS server: whatever the vote's state, it is over and everyone in the lobby is told.
            log.LogInformation("Player {Player} declined the rematch in custom lobby {Lobby}", playerId, lobbyId);
            await redis.KeyDeleteAsync(RematchVotes.LobbyTimerKey(lobbyId));
            await redis.KeyDeleteAsync(RematchVotes.LobbyAcceptKey(lobbyId));
            await DeclinedAsync(redis, humans.All, "");
            return;
        }

        if ((string?)await redis.StringGetAsync(RematchVotes.CasualPlayerKey(playerId)) is not { Length: > 0 } match
            || await ClaimCasualAsync(redis, match) is not { } config)
        {
            log.LogInformation("rematch_decline from {Player}: no rematch to decline", playerId);
            return;
        }

        log.LogInformation("Player {Player} declined the rematch of Casual match {Match}", playerId, match);
        await DeclinedAsync(redis, RematchVotes.Voters(config), match);
    }

    public async Task TimerAsync(string entry, CancellationToken ct)
    {
        var redis = Redis();
        string[] parts = entry.Split(':');
        if (parts is ["lobby", var lobbyId, var matchId])
        {
            if (await ClaimLobbyAsync(redis, lobbyId, matchId))
            {
                log.LogInformation("The rematch timer of custom lobby {Lobby} ran out: starting it", lobbyId);
                await StartLobbyAsync(redis, lobbyId, ct);
            }
        }
        else if (parts is ["casual", var match])
        {
            if (await ClaimCasualAsync(redis, match) is { } config)
            {
                log.LogInformation("The rematch timer of Casual match {Match} ran out: starting it", match);
                await StartCasualAsync(redis, match, config, ct);
            }
        }
        else
        {
            log.LogWarning("Ignoring rematch timer entry {Entry}", entry);
        }
    }

    private static async Task<bool> AllAcceptedAsync(IDatabase redis, string acceptKey, IReadOnlyList<string> voters)
    {
        var accepted = (await redis.SetMembersAsync(acceptKey)).Select(v => v.ToString()).ToHashSet();
        return voters.All(accepted.Contains);
    }

    // The lobby's vote, taken by this caller (true) or already over.
    private static async Task<bool> ClaimLobbyAsync(IDatabase redis, string lobbyId, string matchId)
    {
        if ((long)await redis.ScriptEvaluateAsync(ClaimScript, [RematchVotes.LobbyTimerKey(lobbyId)], [matchId]) != 1)
        {
            return false;
        }

        await redis.KeyDeleteAsync(RematchVotes.LobbyAcceptKey(lobbyId));
        return true;
    }

    // The Casual match's vote, taken by this caller (its config) or already over (null).
    private static async Task<JsonObject?> ClaimCasualAsync(IDatabase redis, string matchId)
    {
        var raw = await redis.StringGetDeleteAsync(RematchVotes.CasualKey(matchId));
        if (raw.IsNullOrEmpty || Js.Parse(raw.ToString()) is not JsonObject config)
        {
            return null;
        }

        await redis.KeyDeleteAsync(RematchVotes.CasualAcceptKey(matchId));
        foreach (string id in RematchVotes.Voters(config))
        {
            await redis.KeyDeleteAsync(RematchVotes.CasualPlayerKey(id));
        }

        return config;
    }

    private async Task StartLobbyAsync(IDatabase redis, string lobbyId, CancellationToken ct)
    {
        if (await lobbies.RematchAsync(lobbyId, ct))
        {
            return;
        }

        log.LogWarning("The rematch in custom lobby {Lobby} could not start: declined for everyone", lobbyId);
        if (await CustomLobbyService.HumansAsync(redis, lobbyId) is { } humans)
        {
            await DeclinedAsync(redis, humans.All, "");
        }
    }

    private async Task StartCasualAsync(IDatabase redis, string matchId, JsonObject config, CancellationToken ct)
    {
        var voters = RematchVotes.Voters(config);
        if (await gate.BlockOutdatedAsync(voters, log, $"the rematch of Casual match {matchId} (declined for everyone)"))
        {
            await DeclinedAsync(redis, voters, matchId);
            return;
        }

        var players = (config["players"] as JsonArray ?? []).OfType<JsonObject>().Select(p => new MatchPlayer(
            RematchVotes.Str(p["playerId"]) ?? "", Int(p["playerIndex"]), Int(p["teamIndex"]), RollbackCallbacks.Truthy(p["isHost"]),
            RematchVotes.Str(p["ip"]) ?? "", RollbackCallbacks.Truthy(p["isBot"]), RollbackCallbacks.Truthy(p["isSpectator"]),
            RematchVotes.Str(p["partyId"]))).ToList();
        string mode = RematchVotes.Str(config["mode"]) ?? "1v1";
        var launched = await launcher.LaunchAsync(new MatchLaunch(mode, MatchmakingMaps.Pick(mode, matchId, log), mode, players,
            GameplayConfigOverride: config["gameplayConfigOverride"] as JsonObject ?? BotDefaults.UnrankedConfigOverride(),
            BotPerks: BotDefaults.PerksArray(), NotificationFields: BotDefaults.UnrankedNotificationFields()), ct);
        if (launched is null)
        {
            log.LogWarning("The rematch of Casual match {Match} could not start: declined for everyone", matchId);
            await DeclinedAsync(redis, voters, matchId);
            return;
        }

        log.LogInformation("Casual match {Match} is the rematch of {Previous}", launched.MatchId, matchId);
    }

    // RematchDeclinedNotification: the game goes back to its menus. A lobby's names no match (as the TS server's).
    private static Task DeclinedAsync(IDatabase redis, IEnumerable<string> playerIds, string matchId) =>
        Task.WhenAll(playerIds.Select(id => PlayerMessages.SendAsync(redis, [id], new JsonObject
        {
            ["data"] = new JsonObject { ["AccountId"] = id, ["MatchId"] = matchId, ["template_id"] = "RematchDeclinedNotification" },
            ["payload"] = new JsonObject
            {
                ["frm"] = new JsonObject { ["id"] = "internal-server", ["type"] = "server-api-key" },
                ["template"] = "realtime",
                ["account_id"] = id,
                ["profile_id"] = id,
            },
            ["header"] = "",
            ["cmd"] = "profile-notification",
        })));

    private static int Int(JsonNode? value) => value is JsonValue v && v.TryGetValue(out double d) ? (int)d : 0;

    private IDatabase Redis() => services.GetService<IConnectionMultiplexer>()?.GetDatabase() ?? throw new InvalidOperationException("this service has no Redis (REDIS)");
}

/// <summary>Starts the rematches whose vote is still open when its timer runs out (<see cref="RematchVotes"/>).</summary>
internal sealed class RematchSweep(IServiceProvider services, IRematches rematches, TimeProvider time, ILogger<RematchSweep> log) : BackgroundService
{
    internal static TimeSpan Interval { get; set; } = TimeSpan.FromMilliseconds(250);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (services.GetService<IConnectionMultiplexer>()?.GetDatabase() is not { } redis)
        {
            log.LogWarning("Rematch timers are not swept from here: this service has no Redis (REDIS)");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(redis, stoppingToken);
            }
            catch (Exception e) when (e is RedisException or TimeoutException or InvalidOperationException or System.Text.Json.JsonException)
            {
                log.LogError("Rematch timers: {Error}", e.Message);
            }

            try
            {
                await Task.Delay(Interval, time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    internal async Task SweepAsync(IDatabase redis, CancellationToken ct)
    {
        long now = time.GetUtcNow().ToUnixTimeMilliseconds();
        foreach (var member in await redis.SortedSetRangeByScoreAsync(RematchVotes.DueKey, double.NegativeInfinity, now, take: 100))
        {
            // Whichever replica removes the entry handles it.
            if (await redis.SortedSetRemoveAsync(RematchVotes.DueKey, member))
            {
                await rematches.TimerAsync(member.ToString(), ct);
            }
        }
    }
}

public static class RematchHosting
{
    /// <summary>rematch_accept and rematch_decline (<see cref="IRematches"/>) and the sweep that starts a rematch when its timer runs out. Needs AddCustomLobbies and AddMatchLauncher.</summary>
    public static WebApplicationBuilder AddRematches(this WebApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.TryAddSingleton<IRematches, Rematches>();
        builder.Services.AddHostedService<RematchSweep>();
        return builder;
    }
}

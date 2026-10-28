using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Lobbies;
using OpenVersus.Server.Core.Matchmaking;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Matches;

/// <summary>
/// Twosday's party breakup, ported from the TS server's twosdayPartyBreakup.ts: for the first breakup_minutes of the
/// window (default 30; Matchmaking/Twosday.cs), every 15 s, each party lobby (lobby:{id}, 2+ players) is broken up as
/// soon as none of its players is queued, in a match or in a ranked set: the owner keeps the lobby, the others get a solo
/// lobby of their own, and both games are told (<see cref="IPartyService.BreakUpAsync"/>, the leave path; its party_left
/// notice is what the client acts on). A party that is busy when the window opens is broken up once it is idle, if that
/// is still within those minutes. Custom, rift and Arena lobbies are never touched. One replica works each round (a 10 s
/// lock); the lobbies service runs it.
/// </summary>
internal sealed class TwosdayPartyBreakup(IServiceProvider services, IPartyService party, TimeProvider time, ILogger<TwosdayPartyBreakup> log) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);
    private const string LockKey = "twosday:breakup_lock";
    private static readonly TimeSpan s_lockTime = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (services.GetService<IConnectionMultiplexer>() is not { } multiplexer)
        {
            return;
        }

        while (!stop.IsCancellationRequested)
        {
            try
            {
                await BreakUpIdlePartiesAsync(multiplexer, time.GetUtcNow());
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError("[Twosday]: party breakup round failed: {Error}", e.Message);
            }

            try
            {
                await Task.Delay(Interval, time, stop);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One round: the party lobbies broken up (0 outside the breakup minutes, or when another replica has the round).</summary>
    internal async Task<int> BreakUpIdlePartiesAsync(IConnectionMultiplexer multiplexer, DateTimeOffset now)
    {
        if (!Twosday.IsEnabled(services))
        {
            return 0;
        }

        var redis = multiplexer.GetDatabase();
        int? into;
        Twosday.Settings settings;
        try
        {
            settings = await Twosday.ReadAsync(redis);
            into = settings.Enabled && settings.BreakupMinutes > 0 ? Twosday.SecondsInto(now, settings.Window) : null;
        }
        catch (Exception e) when (e is RedisException or FormatException or TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            log.LogError("[Twosday]: party breakup skipped: could not read the twosday setting: {Error}", e.Message);
            return 0;
        }

        if (into is not { } seconds || seconds >= settings.BreakupMinutes * 60)
        {
            return 0;
        }

        if (!await redis.StringSetAsync(LockKey, "1", s_lockTime, When.NotExists))
        {
            return 0;
        }

        int brokenUp = 0;
        await foreach (var key in RedisScan.KeysAsync(multiplexer, redis.Database, LobbyStore.Key("*"), pageSize: 200))
        {
            string lobbyId = key.ToString()["lobby:".Length..];
            try
            {
                if (await LobbyStore.GetAsync(redis, lobbyId) is not PartyLobby { PlayerIds.Count: >= 2 } lobby || lobby.OwnerId.Length == 0
                    || await BusyAsync(redis, lobby.PlayerIds))
                {
                    continue;
                }

                log.LogInformation("[Twosday]: breaking up party lobby {Lobby} (players {Players})", lobbyId, string.Join(", ", lobby.PlayerIds));
                await party.BreakUpAsync(lobby, Twosday.PartiesOffMessage);
                brokenUp++;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError("[Twosday]: party breakup: {Key}: {Error}", key.ToString(), e.Message);
            }
        }

        if (brokenUp > 0)
        {
            log.LogInformation("[Twosday]: broke up {Count} party lobbies", brokenUp);
        }

        return brokenUp;
    }

    // Queued, in a match, or between the games of a ranked set: left alone until the next round.
    private static async Task<bool> BusyAsync(IDatabase redis, IEnumerable<string> playerIds)
    {
        foreach (string pid in playerIds)
        {
            if ((string?)await redis.HashGetAsync($"player:{pid}", "status") is "queued" or "in_match"
                || await redis.KeyExistsAsync($"player_ranked_set:{pid}"))
            {
                return true;
            }
        }

        return false;
    }
}

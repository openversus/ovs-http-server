using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Missions;
using OpenVersus.Server.Core.Rifts;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Matches;

// The match flow's side of submit_end_of_match_stats (MatchResults appends each report's result to the stream
// match:results): any number of replicas read it as one consumer group ("matchflow"), so each result is handled once, and
// one appended while no replica runs (a restart, a deploy) waits for the next instead of being lost. Per result:
//   match XP and missions for the reporter (IMissionService; missions with Missions:Enabled), rift progress for the match
//   (IRiftProgressService), each once per match and player (their own SET NX keys), so handling one again is harmless.
//   Then the match's stats (IGameStats), once (game_stats_recorded:{match}, SET NX EX 10 min), when every human of the
//   match (the config's players that are not bots, spectators included) has reported; or, by the sweep every second,
//   30 s after the first report (match_results:due). They come from the first player's report that agrees with the
//   decided winner (MatchWinner), else a spectator's that does, else the first report.
// A result is acknowledged once all of it ran; one that failed stays pending, and any replica takes over results left
// pending a minute (a replica that died, a failure) with XAUTOCLAIM (Redis 6.2+). After 5 deliveries it is logged and
// acknowledged. The stream keeps about the last 10,000 results (XADD MAXLEN ~).

internal sealed class MatchResultStream(IServiceProvider services, IMissionService missions, IOptionsMonitor<MissionSettings> missionSettings,
    IRiftProgressService rifts, IGameStats gameStats, TimeProvider time, ILogger<MatchResultStream> log) : BackgroundService
{
    public const string Group = "matchflow";
    private const int MaxDeliveries = 5;
    private static readonly TimeSpan s_idle = TimeSpan.FromMilliseconds(250);
    internal static TimeSpan ClaimAfter { get; set; } = TimeSpan.FromMinutes(1);
    private readonly string _consumer = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (services.GetService<IConnectionMultiplexer>()?.GetDatabase() is not { } redis)
        {
            log.LogWarning("Match results are not recorded: this service has no Redis (REDIS)");
            return;
        }

        log.LogWarning("MIGRATION BRIDGE: mission and rift progress from match results reach the game through the TS websocket (ws:send); see dotnet/docs/MIGRATION-BRIDGES.md (4)");
        var lastClaim = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EnsureGroupAsync(redis);
                int handled = await ReadAsync(redis, stoppingToken);
                if (time.GetUtcNow() - lastClaim > TimeSpan.FromSeconds(10))
                {
                    lastClaim = time.GetUtcNow();
                    handled += await ClaimAsync(redis, stoppingToken);
                }

                await SweepAsync(redis, stoppingToken);
                if (handled == 0)
                {
                    await Task.Delay(s_idle, time, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                log.LogError(e, "Match results: {Error}", e.Message);
                await Task.Delay(TimeSpan.FromSeconds(1), time, stoppingToken);
            }
        }
    }

    // At the start of the stream, so that results appended before any replica ever ran are read too.
    private bool _grouped;

    internal async Task EnsureGroupAsync(IDatabase redis)
    {
        if (_grouped)
        {
            return;
        }

        try
        {
            await redis.StreamCreateConsumerGroupAsync(MatchResults.Stream, Group, StreamPosition.Beginning, createStream: true);
        }
        catch (RedisServerException e) when (e.Message.Contains("BUSYGROUP", StringComparison.Ordinal))
        {
        }

        _grouped = true;
    }

    internal async Task<int> ReadAsync(IDatabase redis, CancellationToken ct)
    {
        StreamEntry[] entries;
        try
        {
            entries = await redis.StreamReadGroupAsync(MatchResults.Stream, Group, _consumer, StreamPosition.NewMessages, count: 50);
        }
        catch (RedisServerException e) when (e.Message.StartsWith("NOGROUP", StringComparison.Ordinal))
        {
            // The stream (and its group) went away (a flush, a failover with no persistence): made again, from its start.
            log.LogWarning("Match results: the stream's consumer group is gone; made again");
            _grouped = false;
            await EnsureGroupAsync(redis);
            entries = await redis.StreamReadGroupAsync(MatchResults.Stream, Group, _consumer, StreamPosition.NewMessages, count: 50);
        }

        foreach (var entry in entries)
        {
            await HandleAsync(redis, entry, ct);
        }

        return entries.Length;
    }

    // Results another consumer left pending a minute: handled here, or dropped after too many deliveries.
    internal async Task<int> ClaimAsync(IDatabase redis, CancellationToken ct)
    {
        var claimed = await redis.StreamAutoClaimAsync(MatchResults.Stream, Group, _consumer, (long)ClaimAfter.TotalMilliseconds, "0-0", 50);
        foreach (var entry in claimed.ClaimedEntries)
        {
            var pending = await redis.StreamPendingMessagesAsync(MatchResults.Stream, Group, 1, _consumer, entry.Id, entry.Id);
            if (pending.Length > 0 && pending[0].DeliveryCount > MaxDeliveries)
            {
                log.LogError("Match result {Id} failed {Count} times; dropped: {Result}", entry.Id, pending[0].DeliveryCount, (string?)entry["result"]);
                await redis.StreamAcknowledgeAsync(MatchResults.Stream, Group, entry.Id);
                continue;
            }

            await HandleAsync(redis, entry, ct);
        }

        return claimed.ClaimedEntries.Length;
    }

    private async Task HandleAsync(IDatabase redis, StreamEntry entry, CancellationToken ct)
    {
        try
        {
            if (Js.Parse((string?)entry["result"] ?? "null") is JsonObject result && Text(result["matchId"]) is { Length: > 0 } matchId)
            {
                int? winning = MatchWinner.Number(result["winningTeamIndex"]) is { } n ? (int)n : null;
                var counters = result["missionUpdates"] as JsonObject;
                if (Text(result["playerId"]) is { Length: > 0 } playerId)
                {
                    await missions.RecordMatchXpAsync(matchId, playerId, winning, ct);
                    if (missionSettings.CurrentValue.Enabled)
                    {
                        await missions.RecordMatchAsync(matchId, playerId, winning, counters, ct);
                    }
                }

                await rifts.RecordResultAsync(matchId, winning, counters, ct);
                if (await EveryoneReportedAsync(redis, matchId))
                {
                    await RecordStatsAsync(redis, matchId, ct);
                }
            }

            await redis.StreamAcknowledgeAsync(MatchResults.Stream, Group, entry.Id);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogError(e, "Match result {Id} not recorded yet (it stays pending): {Error}", entry.Id, e.Message);
        }
    }

    // Every human of the match (the config's players that are not bots) has reported.
    private static async Task<bool> EveryoneReportedAsync(IDatabase redis, string matchId)
    {
        if (Js.Parse((string?)await redis.StringGetAsync(matchId) ?? "null") is not JsonObject { } config || config["players"] is not JsonArray players)
        {
            return false;
        }

        var humans = players.Where(p => !(p?["isBot"] is JsonValue b && b.GetValueKind() == System.Text.Json.JsonValueKind.True))
            .Select(p => Text(p?["playerId"])).OfType<string>().ToList();
        if (humans.Count == 0)
        {
            return false;
        }

        foreach (string human in humans)
        {
            if (!await redis.HashExistsAsync(MatchResults.ReportsKey(matchId), human))
            {
                return false;
            }
        }

        return true;
    }

    // Matches whose first report is 30 s old: their stats, from whoever reported.
    internal async Task SweepAsync(IDatabase redis, CancellationToken ct)
    {
        var due = await redis.SortedSetRangeByScoreAsync(MatchResults.DueKey, double.NegativeInfinity, time.GetUtcNow().ToUnixTimeMilliseconds(), take: 50);
        foreach (var match in due)
        {
            await RecordStatsAsync(redis, match.ToString(), ct);
        }
    }

    private async Task RecordStatsAsync(IDatabase redis, string matchId, CancellationToken ct)
    {
        if (!await redis.StringSetAsync($"game_stats_recorded:{matchId}", "1", TimeSpan.FromMinutes(10), When.NotExists))
        {
            await redis.SortedSetRemoveAsync(MatchResults.DueKey, matchId);
            return;
        }

        try
        {
            var reports = await MatchResults.ReportEntriesAsync(redis, matchId);
            int? winner = MatchWinner.Resolve(reports.Select(r => r.Report));
            // A report that agrees with the decided winner, a player's before a spectator's, the earliest first.
            var chosen = reports.Where(r => r.Stats is not null).OrderByDescending(r => r.Report.Winner == winner)
                .ThenBy(r => r.Report.Spectator).ThenBy(r => r.Report.Order).FirstOrDefault();
            if (chosen.Stats is null)
            {
                log.LogWarning("Match {Match}: no report with stats to record them from", matchId);
                return;
            }

            await gameStats.RecordAsync(matchId, chosen.Stats, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogError(e, "Game stats for match {Match} not recorded: {Error}", matchId, e.Message);
        }
        finally
        {
            await redis.SortedSetRemoveAsync(MatchResults.DueKey, matchId);
        }
    }

    private static string? Text(JsonNode? value) => value is JsonValue v && v.TryGetValue(out string? s) ? s : null;
}

public static class MatchResultStreamHosting
{
    /// <summary>The match flow's consumer of match:results (missions and match XP, rift progress, the match's stats).</summary>
    public static WebApplicationBuilder AddMatchResultStream(this WebApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.TryAddSingleton<IGameStats, GameStats>();
        builder.Services.AddHostedService<MatchResultStream>();
        return builder;
    }
}

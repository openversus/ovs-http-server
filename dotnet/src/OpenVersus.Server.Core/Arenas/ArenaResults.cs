using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenVersus.Server.Core.Compat;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Arenas;

// An Arena's node match, from its end to its run (the next round is the arenas service's; an Arena match never gets the
// Casual rematch vote):
//   arena_match:{match}  the run's id, written by the arenas service before it launches the match (EX the run's Ttl): what
//                        tells the match flow's MatchEnd that a match is an Arena's
//   arena:results        a stream: MatchEnd appends {runId, matchId, winnerSide} once the match's node has ended it (every
//                        player left it; their reports come before that), winnerSide being the side MatchWinner resolves
//                        from the reports (null: no report)
// The arenas service reads the stream as one consumer group ("arenas"): each record goes to IArenaService.MatchEndedAsync
// (the run's MatchResult under compare-and-set; a second record of the same match is refused there, so handling one again
// is harmless) and is acknowledged once handled. One left pending a minute (a replica that died, a failure) is claimed by
// another with XAUTOCLAIM (Redis 6.2+); after 5 deliveries it is logged and acknowledged, and the run's Match deadline
// decides that match. The stream keeps about the last 10,000 records (XADD MAXLEN ~).
public static class ArenaResults
{
    public const string Stream = "arena:results";
    public const string Field = "result";

    public static string MatchKey(string matchId) => $"arena_match:{matchId}";

    /// <summary>Marks <paramref name="matchId"/> as a match of the run <paramref name="runId"/> (before its launch).</summary>
    public static Task NameMatchAsync(IDatabase redis, string matchId, string runId) => redis.StringSetAsync(MatchKey(matchId), runId, ArenaRunStore.Ttl);

    /// <summary>The run whose match <paramref name="matchId"/> is; null for any other match.</summary>
    public static async Task<string?> RunOfAsync(IDatabase redis, string matchId) =>
        (string?)await redis.StringGetAsync(MatchKey(matchId)) is { Length: > 0 } run ? run : null;

    /// <summary>The match's end, for its run.</summary>
    public static Task AppendAsync(IDatabase redis, string runId, string matchId, int? winnerSide) =>
        redis.StreamAddAsync(Stream, Field, Js.Stringify(new JsonObject { ["runId"] = runId, ["matchId"] = matchId, ["winnerSide"] = winnerSide }),
            maxLength: 10_000, useApproximateMaxLength: true);
}

internal sealed class ArenaResultStream(IServiceProvider services, IArenaService arenas, TimeProvider time, ILogger<ArenaResultStream> log) : BackgroundService
{
    public const string Group = "arenas";
    private const int MaxDeliveries = 5;
    private static readonly TimeSpan s_idle = TimeSpan.FromMilliseconds(250);
    internal static TimeSpan ClaimAfter { get; set; } = TimeSpan.FromMinutes(1);
    private readonly string _consumer = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
    private bool _grouped;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (services.GetService<IConnectionMultiplexer>()?.GetDatabase() is not { } redis)
        {
            log.LogWarning("Arena match results are not read: this service has no Redis (REDIS)");
            return;
        }

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
                log.LogError(e, "Arena match results: {Error}", e.Message);
                await Task.Delay(TimeSpan.FromSeconds(1), time, stoppingToken);
            }
        }
    }

    // At the start of the stream, so that records appended before any replica ever ran are read too.
    internal async Task EnsureGroupAsync(IDatabase redis)
    {
        if (_grouped)
        {
            return;
        }

        try
        {
            await redis.StreamCreateConsumerGroupAsync(ArenaResults.Stream, Group, StreamPosition.Beginning, createStream: true);
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
            entries = await redis.StreamReadGroupAsync(ArenaResults.Stream, Group, _consumer, StreamPosition.NewMessages, count: 50);
        }
        catch (RedisServerException e) when (e.Message.StartsWith("NOGROUP", StringComparison.Ordinal))
        {
            // The stream (and its group) went away (a flush, a failover with no persistence): made again, from its start.
            log.LogWarning("Arena match results: the stream's consumer group is gone; made again");
            _grouped = false;
            await EnsureGroupAsync(redis);
            entries = await redis.StreamReadGroupAsync(ArenaResults.Stream, Group, _consumer, StreamPosition.NewMessages, count: 50);
        }

        foreach (var entry in entries)
        {
            await HandleAsync(redis, entry, ct);
        }

        return entries.Length;
    }

    // Records another consumer left pending a minute: handled here, or dropped after too many deliveries.
    internal async Task<int> ClaimAsync(IDatabase redis, CancellationToken ct)
    {
        var claimed = await redis.StreamAutoClaimAsync(ArenaResults.Stream, Group, _consumer, (long)ClaimAfter.TotalMilliseconds, "0-0", 50);
        foreach (var entry in claimed.ClaimedEntries)
        {
            var pending = await redis.StreamPendingMessagesAsync(ArenaResults.Stream, Group, 1, _consumer, entry.Id, entry.Id);
            if (pending.Length > 0 && pending[0].DeliveryCount > MaxDeliveries)
            {
                log.LogError("Arena match result {Id} failed {Count} times; dropped (the run's Match deadline decides it): {Result}", entry.Id,
                    pending[0].DeliveryCount, (string?)entry[ArenaResults.Field]);
                await redis.StreamAcknowledgeAsync(ArenaResults.Stream, Group, entry.Id);
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
            if (Js.Parse((string?)entry[ArenaResults.Field] ?? "null") is JsonObject result && Text(result["runId"]) is { } runId
                && Text(result["matchId"]) is { } matchId)
            {
                int? side = result["winnerSide"] is JsonValue v && v.TryGetValue(out int n) ? n : null;
                await arenas.MatchEndedAsync(runId, matchId, side, ct);
            }

            await redis.StreamAcknowledgeAsync(ArenaResults.Stream, Group, entry.Id);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogError(e, "Arena match result {Id} not handled yet (it stays pending): {Error}", entry.Id, e.Message);
        }
    }

    private static string? Text(JsonNode? value) => value is JsonValue v && v.TryGetValue(out string? s) && s.Length > 0 ? s : null;
}

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Realtime;

// The services' readers of the realtime gateway's connection events (realtime:connections, GatewayPresence): each service
// that acts on them reads the stream as its own consumer group, so each event is handled once per service whatever the
// number of its replicas. A group is made at the stream's end: events appended before it existed are not replayed
// against players who may be back. An event a replica failed on stays pending and is claimed by another after
// ClaimAfter, and dropped (logged) after MaxDeliveries.

/// <summary>One connection event: its type (connected, replaced, disconnected), the player, the connection, the hash of
/// its session token, when it happened (ms), the client's address, and whether the connection was closed for a gateway
/// node that is gone (GatewayReaper) rather than by its own node.</summary>
public sealed record ConnectionEvent(string Type, string PlayerId, string ConnectionId, string TokenHash, long At, string Ip, bool Reaped = false)
{
    internal static ConnectionEvent? Of(StreamEntry entry) => (string?)entry["player"] is { Length: > 0 } player
        ? new ConnectionEvent((string?)entry["type"] ?? "", player, (string?)entry["connection"] ?? "", (string?)entry["token"] ?? "",
            (long?)entry["at"] ?? 0, (string?)entry["ip"] ?? "", (string?)entry["reaped"] == "1")
        : null;
}

public static class ConnectionEvents
{
    /// <summary>
    /// Why an event about <paramref name="connectionId"/> no longer speaks for the player (or null), and their session's
    /// token as read (null: none). The player has connected again (realtime:conn:{player}: the gateway deletes it when
    /// the current connection closes, so any entry is a newer one), or logged in again (the session's token is not the
    /// one the connection was opened with, <paramref name="tokenHash"/>: /access comes before the websocket).
    /// </summary>
    public static async Task<(string? Back, string? Token)> BackAsync(IDatabase redis, string playerId, string connectionId, string tokenHash)
    {
        if ((string?)await redis.HashGetAsync(GatewayPresence.ConnectionKey(playerId), "id") is { Length: > 0 } current && current != connectionId)
        {
            return ($"connected again ({current})", null);
        }

        string? token = await redis.HashGetAsync($"connections:{playerId}", "jwt");
        if (tokenHash.Length > 0 && token is { Length: > 0 } && GatewayPresence.TokenHash(token) != tokenHash)
        {
            return ("logged in again", token);
        }

        return (null, token);
    }
}

/// <summary>A service's reader of realtime:connections (see the header of ConnectionEvents.cs).</summary>
internal abstract class ConnectionEventsReader(IServiceProvider services, TimeProvider time, ILogger logger) : BackgroundService
{
    protected IServiceProvider Services { get; } = services;
    protected TimeProvider Time { get; } = time;
    protected ILogger Log { get; } = logger;

    private const int MaxDeliveries = 5;
    private static readonly TimeSpan s_idle = TimeSpan.FromMilliseconds(50);
    internal static TimeSpan ClaimAfter { get; set; } = TimeSpan.FromSeconds(10);
    private readonly string _consumer = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
    private bool _grouped;

    /// <summary>The consumer group: the service's name.</summary>
    protected abstract string Group { get; }

    /// <summary>The stream, a test's own in tests (the gateway's tests append to it in the same database).</summary>
    internal string StreamKey { get; init; } = GatewayPresence.ConnectionsStream;

    /// <summary>One event. Throwing a Redis error or a timeout leaves it pending, to be tried again.</summary>
    protected abstract Task OnEventAsync(IDatabase redis, ConnectionEvent connectionEvent);

    /// <summary>Anything else the reader does on its loop (about once a second); the number of things it did.</summary>
    protected virtual Task<int> EverySecondAsync(IDatabase redis) => Task.FromResult(0);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (Services.GetService<IConnectionMultiplexer>()?.GetDatabase() is not { } redis)
        {
            Log.LogWarning("Connection events are not read here: this service has no Redis (REDIS)");
            return;
        }

        DateTimeOffset lastClaim = DateTimeOffset.MinValue, lastSecond = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EnsureGroupAsync(redis);
                int handled = await ReadAsync(redis);
                if (Time.GetUtcNow() - lastClaim > TimeSpan.FromSeconds(2))
                {
                    lastClaim = Time.GetUtcNow();
                    handled += await ClaimAsync(redis);
                }

                if (Time.GetUtcNow() - lastSecond > TimeSpan.FromSeconds(1))
                {
                    lastSecond = Time.GetUtcNow();
                    handled += await EverySecondAsync(redis);
                }

                if (handled == 0)
                {
                    await Task.Delay(s_idle, Time, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                Log.LogError(e, "Connection events ({Group}): {Error}", Group, e.Message);
                await Task.Delay(TimeSpan.FromSeconds(1), Time, stoppingToken);
            }
        }
    }

    internal async Task EnsureGroupAsync(IDatabase redis)
    {
        if (_grouped)
        {
            return;
        }

        try
        {
            await redis.StreamCreateConsumerGroupAsync(StreamKey, Group, StreamPosition.NewMessages, createStream: true);
        }
        catch (RedisServerException e) when (e.Message.Contains("BUSYGROUP", StringComparison.Ordinal))
        {
        }

        _grouped = true;
    }

    internal async Task<int> ReadAsync(IDatabase redis)
    {
        StreamEntry[] entries;
        try
        {
            entries = await redis.StreamReadGroupAsync(StreamKey, Group, _consumer, StreamPosition.NewMessages, count: 50);
        }
        catch (RedisServerException e) when (e.Message.StartsWith("NOGROUP", StringComparison.Ordinal))
        {
            // The stream (and its group) went away (a flush, a failover with no persistence): made again, at its end.
            Log.LogWarning("Connection events ({Group}): the stream's consumer group is gone; made again", Group);
            _grouped = false;
            await EnsureGroupAsync(redis);
            entries = await redis.StreamReadGroupAsync(StreamKey, Group, _consumer, StreamPosition.NewMessages, count: 50);
        }

        foreach (var entry in entries)
        {
            await HandleAsync(redis, entry);
        }

        return entries.Length;
    }

    // Events another consumer left pending: handled here, or dropped after too many deliveries.
    internal async Task<int> ClaimAsync(IDatabase redis)
    {
        var claimed = await redis.StreamAutoClaimAsync(StreamKey, Group, _consumer, (long)ClaimAfter.TotalMilliseconds, "0-0", 50);
        foreach (var entry in claimed.ClaimedEntries)
        {
            var pending = await redis.StreamPendingMessagesAsync(StreamKey, Group, 1, _consumer, entry.Id, entry.Id);
            if (pending.Length > 0 && pending[0].DeliveryCount > MaxDeliveries)
            {
                Log.LogError("Connection event {Type} of {Player} failed {Count} times in {Group}; dropped", (string?)entry["type"], (string?)entry["player"],
                    pending[0].DeliveryCount, Group);
                await redis.StreamAcknowledgeAsync(StreamKey, Group, entry.Id);
                continue;
            }

            await HandleAsync(redis, entry);
        }

        return claimed.ClaimedEntries.Length;
    }

    private async Task HandleAsync(IDatabase redis, StreamEntry entry)
    {
        try
        {
            if (ConnectionEvent.Of(entry) is { } connectionEvent)
            {
                await OnEventAsync(redis, connectionEvent);
            }

            await redis.StreamAcknowledgeAsync(StreamKey, Group, entry.Id);
        }
        catch (Exception e) when (e is RedisException or TimeoutException)
        {
            Log.LogError("Connection event {Type} of {Player} not handled yet in {Group} (it stays pending): {Error}", (string?)entry["type"],
                (string?)entry["player"], Group, e.Message);
        }
    }
}

using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenVersus.Server.Core.Compat;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Realtime;

// Websocket messages sent a moment later (the TS websocket's setTimeout sends: a match end's +500, +1000 and +1500 ms),
// kept in Redis so that a restart or another replica still sends them: realtime:due, a sorted set scored by when each is
// due (ms), each member {id, playerIds, message}, or {id, channel: "ws:disconnect", message} for a connection to close
// (ScheduleDisconnectAsync: the request is published as it is). The sweep (DelayedMessageSweep, in the executables that schedule them)
// looks every 100 ms; an entry is sent by whichever replica removes it first (ZREM), so once. 100 ms keeps the order of
// sends 500 ms apart; a restart delays what fell due meanwhile, and sends it.
//
// Redis, written  realtime:due (ZADD; ZREM when sent)
// Published       ws:send (PlayerMessages.SendAsync); ws:disconnect (PlayerMessages.DisconnectAsync)

public static class DelayedMessages
{
    public const string Key = "realtime:due";

    /// <summary>Sends <paramref name="message"/> to <paramref name="playerIds"/> through ws:send once <paramref name="delay"/> has passed.</summary>
    public static Task ScheduleAsync(IDatabase redis, TimeProvider time, IEnumerable<string> playerIds, JsonObject message, TimeSpan delay)
    {
        var entry = new JsonObject
        {
            ["id"] = Guid.NewGuid().ToString("N"),
            ["playerIds"] = new JsonArray([.. playerIds.Select(id => (JsonNode)id)]),
            ["message"] = message,
        };
        return redis.SortedSetAddAsync(Key, Js.Stringify(entry), (time.GetUtcNow() + delay).ToUnixTimeMilliseconds());
    }

    /// <summary>Publishes <paramref name="request"/> on ws:disconnect ({playerId, connectionId?, code?, reason?}) once <paramref name="delay"/> has passed.</summary>
    public static Task ScheduleDisconnectAsync(IDatabase redis, TimeProvider time, JsonObject request, TimeSpan delay)
    {
        var entry = new JsonObject
        {
            ["id"] = Guid.NewGuid().ToString("N"),
            ["channel"] = GatewayChannels.Disconnect,
            ["message"] = request,
        };
        return redis.SortedSetAddAsync(Key, Js.Stringify(entry), (time.GetUtcNow() + delay).ToUnixTimeMilliseconds());
    }
}

/// <summary>Sends the delayed messages that fell due (<see cref="DelayedMessages"/>).</summary>
internal sealed class DelayedMessageSweep(IServiceProvider services, TimeProvider time, ILogger<DelayedMessageSweep> log) : BackgroundService
{
    internal static TimeSpan Interval { get; set; } = TimeSpan.FromMilliseconds(100);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (services.GetService<IConnectionMultiplexer>()?.GetDatabase() is not { } redis)
        {
            log.LogWarning("Delayed websocket messages are not sent from here: this service has no Redis (REDIS)");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(redis);
            }
            catch (Exception e) when (e is RedisException or TimeoutException)
            {
                log.LogError("Delayed websocket messages: {Error}", e.Message);
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

    internal async Task SweepAsync(IDatabase redis)
    {
        long now = time.GetUtcNow().ToUnixTimeMilliseconds();
        foreach (var member in await redis.SortedSetRangeByScoreAsync(DelayedMessages.Key, double.NegativeInfinity, now, take: 100))
        {
            if (!await redis.SortedSetRemoveAsync(DelayedMessages.Key, member))
            {
                continue;
            }

            if (Js.Parse(member.ToString()) is JsonObject { } entry && entry["message"] is JsonObject message)
            {
                if ((string?)entry["channel"] == GatewayChannels.Disconnect)
                {
                    await PlayerMessages.DisconnectAsync(redis, message);
                    continue;
                }

                await PlayerMessages.SendAsync(redis, (entry["playerIds"] as JsonArray ?? []).Select(id => id?.ToString() ?? ""), message.DeepClone().AsObject());
            }
        }
    }
}

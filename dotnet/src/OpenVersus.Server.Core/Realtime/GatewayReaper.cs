using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenVersus.Server.Core.Hosting;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Realtime;

// The players of a gateway node that died (GatewayPresence's header): every node, every Gateway:ReapIntervalMs, looks at
// the players who have not answered a ping for Gateway:ReapAfterMs (player_heartbeats) and takes offline, as their
// connection's close would have, those whose current connection (realtime:conn:{player}) is on a node that is gone: a
// disconnected event each, marked reaped, which the readers handle as any close (the lobbies, the queue, the session) but
// for the match, where it is a crash. A player who has no connection entry left at all (it ran out: no node reaped them
// for 3 minutes) is reaped too, with no connection, node or session token in the event.
//
// A node is gone when the instance registry says it stopped (a clean stop closes its connections first; anything left of
// them stays), or when its entry (ovs:instance:{node}, rewritten every 5 s, EX 20 s) has been missing for 10 s, across
// this node's own looks: a Redis outage lets a live node's entry run out, and the node writes it again within 5 s of
// Redis coming back. A player whose node is up is that node's to close (Gateway:SilenceCutoffMs); this node never reaps
// its own players. Each reap is one script that acts only while the connection is still the player's current one on that
// node, so several nodes reaping at once reap each player once, and a player who connected again (or whose connection
// the edge moved to another node, under the same id) is left alone.
//
// Redis, read     player_heartbeats (ZRANGEBYSCORE, 200 a look); realtime:conn:{player}; ovs:instance:{node}
// Redis, written  GatewayPresence.ReapAsync's: realtime:conn:{player} (DEL), player_heartbeats (ZREM), online_players
//                 (SREM) and active_ip_accounts:{ip} (ZREM) but in a post-match window (rejoin_pending:{player}), and
//                 realtime:connections (XADD disconnected, reaped "1")

/// <summary>Takes offline the players of gateway nodes that are gone (see the header of GatewayReaper.cs).</summary>
internal sealed class GatewayReaper(IServiceProvider services, IOptionsMonitor<GatewaySettings> settings, ServiceInstance instance, TimeProvider time,
    ILogger<GatewayReaper> log) : BackgroundService
{
    private const int Batch = 200;

    /// <summary>How long a node's registry entry must be missing before its players are reaped: two of its rewrites.</summary>
    internal static readonly TimeSpan MissingFor = InstanceRegistry.Interval * 2;

    // When this node first saw each missing node's entry gone (forgotten once it is back, or after the registry's memory).
    private readonly Dictionary<string, DateTimeOffset> _missingSince = new(StringComparer.Ordinal);

    /// <summary>The stream the events go to, a test's own in tests.</summary>
    internal string StreamKey { get; init; } = GatewayPresence.ConnectionsStream;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (services.GetService<IConnectionMultiplexer>()?.GetDatabase() is not { } redis)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(settings.CurrentValue.ReapIntervalMs), time, stoppingToken);
                await SweepAsync(redis);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e) when (e is RedisException or TimeoutException)
            {
                log.LogWarning("Looking for the players of gateway nodes that are gone: {Error}", e.Message);
            }
        }
    }

    /// <summary>One look; the number of players reaped.</summary>
    internal async Task<int> SweepAsync(IDatabase redis)
    {
        var now = time.GetUtcNow();
        long answeredBefore = now.ToUnixTimeMilliseconds() - settings.CurrentValue.ReapAfterMs;
        var silent = await redis.SortedSetRangeByScoreAsync(GatewayPresence.Heartbeats, double.NegativeInfinity, answeredBefore, take: Batch);
        var gone = new Dictionary<string, bool>(StringComparer.Ordinal);
        int reaped = 0;
        foreach (var member in silent)
        {
            string player = member.ToString();
            var entry = (await redis.HashGetAllAsync(GatewayPresence.ConnectionKey(player))).ToDictionary(e => e.Name.ToString(), e => e.Value.ToString());
            string id = entry.GetValueOrDefault("id", ""), node = entry.GetValueOrDefault("node", "");
            if (id.Length > 0)
            {
                if (!gone.TryGetValue(node, out bool isGone))
                {
                    gone[node] = isGone = await GoneAsync(redis, node, now);
                }

                if (!isGone)
                {
                    continue;
                }
            }

            string ip = entry.GetValueOrDefault("ip", "");
            if (await GatewayPresence.ReapAsync(redis, player, id, node, ip, entry.GetValueOrDefault("token", ""), answeredBefore, now, StreamKey))
            {
                reaped++;
                if (id.Length > 0)
                {
                    log.LogWarning("Player {Player} with IP {Ip} was on gateway node {Node}, which is gone: connection {Connection} closed for it", player, ip, node, id);
                }
                else
                {
                    log.LogWarning("Player {Player} had no connection left (its entry ran out) and was still online: taken offline", player);
                }
            }
        }

        foreach (var (node, since) in _missingSince.Where(m => now - m.Value > InstanceRegistry.Remembered).ToList())
        {
            _missingSince.Remove(node);
        }

        return reaped;
    }

    private async Task<bool> GoneAsync(IDatabase redis, string node, DateTimeOffset now)
    {
        if (node == instance.Id)
        {
            return false;
        }

        if ((string?)await redis.StringGetAsync(InstanceRegistry.Key(node)) is { } report)
        {
            _missingSince.Remove(node);
            return (JsonNode.Parse(report) as JsonObject)?["state"]?.GetValue<string>() == "Stopped";
        }

        if (!_missingSince.TryGetValue(node, out var since))
        {
            _missingSince[node] = now;
            return false;
        }

        return now - since >= MissingFor;
    }
}

public static class GatewayReaperHosting
{
    /// <summary>The gateway's reaper of the players of nodes that are gone; needs the Gateway settings.</summary>
    public static WebApplicationBuilder AddGatewayReaper(this WebApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddHostedService<GatewayReaper>();
        return builder;
    }
}

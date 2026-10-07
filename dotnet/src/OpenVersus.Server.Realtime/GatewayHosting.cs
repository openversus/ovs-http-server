using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OpenVersus.Server.Core.Access;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Control;
using OpenVersus.Server.Core.Hydra;
using OpenVersus.Server.Core.Realtime;
using OpenVersus.Server.Core.Settings;
using StackExchange.Redis;

namespace OpenVersus.Server.Realtime;

public static class GatewayHosting
{
    public static WebApplicationBuilder AddGateway(this WebApplicationBuilder builder)
    {
        builder.AddSetting<GatewaySettings>("Gateway");
        // The session token's secret, shared with every service that reads tokens.
        builder.AddSetting<AccessSettings>("Access");
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<GatewayNode>();
        builder.Services.AddHostedService<GatewayPings>();
        builder.Services.AddHostedService<GatewayTicks>();
        builder.Services.AddHostedService<GatewaySubscriber>();
        builder.AddGatewayReaper();
        return builder;
    }

    /// <summary>
    /// Every websocket upgrade on the public listener, whatever its path (the TS websocket server took any), is a game
    /// connection; any other request there but /health/* is answered as the TS websocket server answered it (200,
    /// "HTTP server is running"), which a load balancer's check may rely on. Websocket keep-alive frames are off: the
    /// game is pinged with its own message (<see cref="GatewayPings"/>).
    /// </summary>
    public static WebApplication UseGateway(this WebApplication app)
    {
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.Zero });
        var node = app.Services.GetRequiredService<GatewayNode>();
        app.Use(async (context, next) =>
        {
            if (ControlListeners.IsControl(context) || context.Request.Path.StartsWithSegments("/health"))
            {
                await next(context);
            }
            else if (context.WebSockets.IsWebSocketRequest)
            {
                await node.HandleAsync(context);
            }
            else
            {
                context.Response.ContentType = "text/plain";
                await context.Response.WriteAsync("HTTP server is running\n");
            }
        });
        return app;
    }
}

/// <summary>
/// Pings every connection this node holds, every Gateway:PingIntervalMs. A game that has not answered for
/// Gateway:SilenceCutoffMs is gone even if its socket never closed: the connection is dropped, and its close does the rest.
/// </summary>
internal sealed class GatewayPings(GatewayNode node, IOptionsMonitor<GatewaySettings> settings, TimeProvider time, ILogger<GatewayPings> log) : BackgroundService
{
    private static readonly byte[] s_ping = [GatewayProtocol.Ping];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(settings.CurrentValue.PingIntervalMs), time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            long now = time.GetUtcNow().ToUnixTimeMilliseconds();
            int cutoff = settings.CurrentValue.SilenceCutoffMs;
            foreach (var connection in node.Connections)
            {
                long silent = now - connection.LastAnswerMs;
                if (silent >= cutoff)
                {
                    log.LogWarning("Player {Player} with IP {Ip} has not answered for {Seconds} s; closing the connection",
                        connection.Info.PlayerId, connection.Info.Ip, Math.Round(silent / 1000.0));
                    connection.Abort();
                    continue;
                }

                if (!connection.Send(s_ping))
                {
                    log.LogWarning("Player {Player} with IP {Ip} stopped reading its messages; connection dropped", connection.Info.PlayerId, connection.Info.Ip);
                }
            }
        }
    }
}

/// <summary>
/// The "still searching" tick (the TS websocket's handleMatchTick): every second, each player held here who has a ticket in
/// a matchmaking queue (realtime:queued, MatchmakingQueue) is sent matchmaking-tick with its request id: one HMGET per
/// second for the node's players, whoever queued them.
/// </summary>
internal sealed class GatewayTicks(IServiceProvider services, GatewayNode node, TimeProvider time, ILogger<GatewayTicks> log) : BackgroundService
{
    internal static TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(1);

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
                await Task.Delay(Interval, time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                await TickAsync(redis);
            }
            catch (Exception e) when (e is RedisException or TimeoutException)
            {
                log.LogError("Matchmaking ticks: {Error}", e.Message);
            }
        }
    }

    internal async Task TickAsync(IDatabase redis)
    {
        var held = node.Connections.ToList();
        if (held.Count == 0)
        {
            return;
        }

        var tickets = await redis.HashGetAsync(Core.Matches.MatchmakingQueue.QueuedKey, [.. held.Select(c => (RedisValue)c.Info.PlayerId)]);
        var encoded = new Dictionary<string, byte[]>();
        for (int i = 0; i < held.Count; i++)
        {
            if (tickets[i].IsNullOrEmpty)
            {
                continue;
            }

            string ticket = tickets[i].ToString();
            if (!encoded.TryGetValue(ticket, out var bytes))
            {
                try
                {
                    bytes = HydraEncoder.Encode(Core.Matches.MatchmakingQueue.Tick(Core.Matches.MatchmakingQueue.RequestIdOf(ticket)), webSocket: true);
                }
                catch (Exception e) when (e is System.Text.Json.JsonException or HydraFormatException or InvalidOperationException)
                {
                    log.LogError("Player {Player}'s queue ticket is not one: {Error}", held[i].Info.PlayerId, e.Message);
                    continue;
                }

                encoded[ticket] = bytes;
            }

            if (!held[i].Send(bytes))
            {
                log.LogWarning("Player {Player} with IP {Ip} stopped reading its messages; connection dropped", held[i].Info.PlayerId, held[i].Info.Ip);
            }
        }
    }
}

/// <summary>
/// What every node hears: <c>ws:send</c> (a message for players: sent to those whose connection is here) and
/// <c>ws:disconnect</c> (close a player's connection: done by the node that holds it). Each channel's messages are
/// handled one at a time, in the order they were published.
/// </summary>
internal sealed class GatewaySubscriber(IServiceProvider services, GatewayNode node, ILogger<GatewaySubscriber> log) : IHostedService
{
    private readonly List<ChannelMessageQueue> _queues = [];

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (services.GetService<IConnectionMultiplexer>() is not { } mux)
        {
            log.LogWarning("Nothing is delivered from here: this service has no Redis (REDIS)");
            return;
        }

        var subscriber = mux.GetSubscriber();
        var send = await subscriber.SubscribeAsync(RedisChannel.Literal(GatewayChannels.Send));
        send.OnMessage(message => Deliver(message.Message.ToString()));
        var disconnect = await subscriber.SubscribeAsync(RedisChannel.Literal(GatewayChannels.Disconnect));
        disconnect.OnMessage(message => Disconnect(message.Message.ToString()));
        _queues.AddRange([send, disconnect]);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var queue in _queues)
        {
            await queue.UnsubscribeAsync();
        }
    }

    // {playerIds, message}: message encoded once (a JS object's key order, as the TS websocket's JSON.parse gave it),
    // then queued for each named player held here.
    private void Deliver(string json)
    {
        byte[] bytes;
        JsonArray? playerIds;
        try
        {
            if (Js.Parse(json) is not JsonObject send)
            {
                log.LogError("Bad ws:send message: not a JSON object");
                return;
            }

            playerIds = send["playerIds"] as JsonArray;
            bytes = HydraEncoder.Encode(send["message"], webSocket: true);
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or HydraFormatException or InvalidOperationException)
        {
            log.LogError("Bad ws:send message: {Error}", e.Message);
            return;
        }

        foreach (var id in playerIds ?? [])
        {
            if (id?.GetValueKind() != System.Text.Json.JsonValueKind.String || !node.TryGet((string)id!, out var connection))
            {
                continue;
            }

            if (!connection.Send(bytes))
            {
                log.LogWarning("Player {Player} with IP {Ip} stopped reading its messages; connection dropped", connection.Info.PlayerId, connection.Info.Ip);
            }
        }
    }

    private void Disconnect(string json)
    {
        JsonObject request;
        try
        {
            request = Js.Parse(json) as JsonObject ?? throw new InvalidOperationException("not a JSON object");
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException)
        {
            log.LogError("Bad ws:disconnect message: {Error}", e.Message);
            return;
        }

        string? playerId = Text(request["playerId"]);
        string? only = Text(request["connectionId"]), except = Text(request["except"]);
        if (playerId is null || !node.TryGet(playerId, out var connection))
        {
            if (except is null)
            {
                log.LogInformation("Forced disconnect of {Player}: not connected to this node", playerId);
            }

            return;
        }

        if ((only is not null && only != connection.Info.Id) || except == connection.Info.Id)
        {
            return;
        }

        if (request["code"] is { } code && code.GetValueKind() == System.Text.Json.JsonValueKind.Number)
        {
            var status = (WebSocketCloseStatus)(int)code.GetValue<double>();
            log.LogInformation("Closing player {Player}'s connection {Connection}: {Reason}", playerId, connection.Info.Id, Text(request["reason"]));
            connection.Close(status, Text(request["reason"]));
            return;
        }

        log.LogWarning("Forced disconnect of {Player} with IP {Ip} (administrator); closing the connection", playerId, connection.Info.Ip);
        connection.Abort();
    }

    private static string? Text(JsonNode? node) => node?.GetValueKind() == System.Text.Json.JsonValueKind.String ? (string)node! : null;
}

using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OpenVersus.Server.Core.Access;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Control;
using OpenVersus.Server.Core.Hosting;
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

    // WEBSOCKET_PORT, or when that is 0 the port the server bound for it (its listeners but the control ones).
    private static int? PublicPort(WebApplication app)
    {
        var service = KnownServices.Realtime;
        int configured = app.Configuration.GetValue<int?>(service.PublicPortKey!) ?? service.DefaultPublicPort;
        if (configured != 0)
        {
            return configured;
        }

        int? control = app.Services.GetRequiredService<ControlListeners.Bound>().Port;
        var addresses = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>().Features
            .Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()?.Addresses ?? [];
        return addresses.Select(BindingAddress.Parse).Where(a => !a.IsUnixPipe && a.Port != control).Select(a => (int?)a.Port).FirstOrDefault();
    }

    // WEBSOCKET_ADVERTISE: a host, or a network (CIDR: this node's address inside it, for a container on several
    // networks); unset, the first IPv4 address of an interface that is up and not a loopback; else 127.0.0.1.
    internal static string AdvertisedHost(IConfiguration configuration)
    {
        string? advertise = configuration["WEBSOCKET_ADVERTISE"];
        System.Net.IPNetwork? network = null;
        if (advertise is { Length: > 0 })
        {
            if (!System.Net.IPNetwork.TryParse(advertise, out var parsed))
            {
                return advertise;
            }

            network = parsed;
        }

        var addresses = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(i => i.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                && i.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
            .SelectMany(i => i.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(a));
        return (network is { } within ? addresses.FirstOrDefault(within.Contains) : addresses.FirstOrDefault())?.ToString() ?? "127.0.0.1";
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

        // Where an edge reaches this node (the instance registry carries it): its public port, on the host
        // WEBSOCKET_ADVERTISE names, or this machine's (container's) first IPv4 address that is not a loopback.
        var instance = app.Services.GetRequiredService<ServiceInstance>();
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            instance.Address = PublicPort(app) is { } port ? $"ws://{AdvertisedHost(app.Configuration)}:{port}" : null;
            app.Logger.LogInformation("Edges reach this node at {Address}", instance.Address ?? "(no public listener found)");
        });
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

    // {playerIds, message, seqs?}: message encoded once (a JS object's key order, as the TS websocket's JSON.parse gave
    // it), then queued for each named player held here, with the player's entry in their replay log (seqs) for an edge.
    private void Deliver(string json)
    {
        byte[] bytes;
        JsonArray? playerIds;
        JsonObject? seqs;
        try
        {
            if (Js.Parse(json) is not JsonObject send)
            {
                log.LogError("Bad ws:send message: not a JSON object");
                return;
            }

            playerIds = send["playerIds"] as JsonArray;
            seqs = send["seqs"] as JsonObject;
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

            if (!connection.Deliver(bytes, StreamId.TryParse(Text(seqs?[(string)id!]), out var seq) ? seq : null))
            {
                log.LogWarning("Player {Player} with IP {Ip} stopped reading its messages; connection dropped", connection.Info.PlayerId, connection.Info.Ip);
            }
        }
    }

    private void Disconnect(string json)
    {
        GatewayDisconnect request;
        try
        {
            request = GatewayDisconnect.Parse(json);
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException)
        {
            log.LogError("Bad ws:disconnect message: {Error}", e.Message);
            return;
        }

        if (request.PlayerId is null || !node.TryGet(request.PlayerId, out var connection))
        {
            if (request.Except is null)
            {
                log.LogInformation("Forced disconnect of {Player}: not connected to this node", request.PlayerId);
            }

            return;
        }

        if (!request.AppliesTo(connection.Info.Id))
        {
            return;
        }

        if (request.Code is not null)
        {
            log.LogInformation("Closing player {Player}'s connection {Connection}: {Reason}", request.PlayerId, connection.Info.Id, request.Reason);
        }
        else
        {
            log.LogWarning("Forced disconnect of {Player} with IP {Ip} (administrator); closing the connection", request.PlayerId, connection.Info.Ip);
        }

        connection.Disconnect(request.Code, request.Reason, request.Seq);
    }

    private static string? Text(JsonNode? node) => node?.GetValueKind() == System.Text.Json.JsonValueKind.String ? (string)node! : null;
}

/// <summary>
/// A ws:disconnect request ({playerId, connectionId?, except?, code?, reason?, seq?}), live or replayed from the player's
/// log: it applies to a connection only if it is <c>connectionId</c> and is not <c>except</c>, when those are given.
/// </summary>
internal sealed record GatewayDisconnect(string? PlayerId, string? Only, string? Except, int? Code, string? Reason, StreamId? Seq)
{
    public static GatewayDisconnect Parse(string json)
    {
        var request = Js.Parse(json) as JsonObject ?? throw new InvalidOperationException("not a JSON object");
        return new GatewayDisconnect(Text(request["playerId"]), Text(request["connectionId"]), Text(request["except"]),
            request["code"] is { } code && code.GetValueKind() == System.Text.Json.JsonValueKind.Number ? (int)code.GetValue<double>() : null,
            Text(request["reason"]), StreamId.TryParse(Text(request["seq"]), out var seq) ? seq : null);
    }

    public bool AppliesTo(string connectionId) => (Only is null || Only == connectionId) && Except != connectionId;

    private static string? Text(JsonNode? node) => node?.GetValueKind() == System.Text.Json.JsonValueKind.String ? (string)node! : null;
}

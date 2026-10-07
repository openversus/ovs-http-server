using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenVersus.Server.Core.Access;
using OpenVersus.Server.Core.Hosting;
using OpenVersus.Server.Core.Realtime;
using StackExchange.Redis;

namespace OpenVersus.Server.Realtime;

/// <summary>
/// The game sockets this node holds, by player: each player's current connection here. A connection lives from its
/// handshake (the game's first frame: its session token, checked as every service checks it) until either side closes
/// it; while it lives it is pinged (<see cref="GatewayPings"/>) and gets what <c>ws:send</c> names its player for
/// (<see cref="GatewaySubscriber"/>). Presence and the connection events are <see cref="GatewayPresence"/>'s.
/// </summary>
internal sealed class GatewayNode(IServiceProvider services, IOptionsMonitor<GatewaySettings> settings, IOptionsMonitor<AccessSettings> access,
    ServiceInstance instance, TimeProvider time, IHostApplicationLifetime lifetime, ILogger<GatewayNode> log)
{
    // The first frame holds a session token (about 1 KB); anything this long is not a game.
    private const int MaxMessageBytes = 64 * 1024;
    private static readonly TimeSpan s_closeWait = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<string, GatewayConnection> _current = new();

    /// <summary>The current connections held here.</summary>
    public ICollection<GatewayConnection> Connections => _current.Values;

    public bool TryGet(string playerId, out GatewayConnection connection) => _current.TryGetValue(playerId, out connection!);

    public async Task HandleAsync(HttpContext context)
    {
        string ip = ClientAddress.Of(context, stripMapped: true);

        // An edge's link to a game (GatewayEdge): trusted only with the shared secret, and named by the edge.
        string? edgeConnectionId = null;
        if (context.Request.Headers.ContainsKey(GatewayEdge.SecretHeader))
        {
            if (!GatewayEdge.SecretMatches(context.Request.Headers[GatewayEdge.SecretHeader], settings.CurrentValue.EdgeSecret))
            {
                log.LogWarning("Refused an edge link from {Remote}: its secret is not Gateway:EdgeSecret (or none is set)", context.Connection.RemoteIpAddress);
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            edgeConnectionId = context.Request.Headers[GatewayEdge.ConnectionIdHeader];
            if (!GatewayEdge.IsConnectionId(edgeConnectionId))
            {
                log.LogWarning("Refused an edge link from {Remote}: no usable {Header}", context.Connection.RemoteIpAddress, GatewayEdge.ConnectionIdHeader);
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            if (context.Request.Headers.ContainsKey(GatewayEdge.ResumeAfterHeader))
            {
                log.LogWarning("Refused an edge's resume of connection {Connection}: this node does not resume links yet", edgeConnectionId);
                context.Response.StatusCode = StatusCodes.Status501NotImplemented;
                return;
            }
        }

        bool edge = edgeConnectionId is not null;
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var stopping = lifetime.ApplicationStopping;
        if (services.GetService<IConnectionMultiplexer>()?.GetDatabase() is not { } redis)
        {
            log.LogError("Refused the websocket from {Ip}: this service has no Redis (REDIS)", ip);
            await CloseAsync(socket, WebSocketCloseStatus.InternalServerError, "no redis");
            return;
        }

        byte[]? first;
        using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(stopping))
        {
            handshake.CancelAfter(settings.CurrentValue.HandshakeTimeoutMs);
            try
            {
                first = await ReceiveAsync(socket, handshake.Token);
            }
            catch (OperationCanceledException) when (!stopping.IsCancellationRequested)
            {
                log.LogWarning("The websocket from {Ip} sent no first frame within {Ms} ms; closed", ip, settings.CurrentValue.HandshakeTimeoutMs);
                socket.Abort();
                return;
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException)
            {
                return;
            }
        }

        if (first is null)
        {
            return;
        }

        string token, playerId;
        try
        {
            token = GatewayProtocol.TokenOf(first);
            string secret = access.CurrentValue.JwtSecret ?? throw new AccessTokenException("Access:JwtSecret is not set");
            var claims = AccessTokens.Verify(token, secret, time.GetUtcNow());
            playerId = claims["id"] is { } id && id.GetValueKind() == JsonValueKind.String ? (string)id! : throw new AccessTokenException("the token names no player");
        }
        catch (Exception e) when (e is FormatException or AccessTokenException)
        {
            // Nothing is sent before the close. The TS websocket's close carried no code; .NET cannot send a close frame
            // without one (it writes 1005, which the protocol forbids on the wire and a client refuses), so 1000.
            log.LogWarning("Rejected the websocket handshake from {Ip}: {Error}", ip, e.Message);
            if (edge)
            {
                // The token is as bad on every node: the edge closes the game rather than trying another.
                await SendAsync(socket, GatewayEdge.Close((int)WebSocketCloseStatus.NormalClosure, null));
            }

            await CloseAsync(socket, WebSocketCloseStatus.NormalClosure, null);
            return;
        }

        var info = new GatewayConnectionInfo(edgeConnectionId ?? Guid.NewGuid().ToString("N"), playerId, instance.Id, ip, GatewayPresence.TokenHash(token),
            Guid.NewGuid().ToString("N"));
        var connection = new GatewayConnection(socket, info, time.GetUtcNow().ToUnixTimeMilliseconds(), edge);
        var writer = connection.WriteAsync(stopping);
        if (edge)
        {
            // The claim first: the edge's position (the head of the player's log at the claim) goes before the id frame,
            // so an edge that never got it knows the game never got the id frame either.
            GatewayClaim? claim = null;
            await RecordAsync("handshake", playerId, async () => claim = await GatewayPresence.ConnectedAsync(redis, info, time.GetUtcNow()));
            connection.SendPosition(claim?.LogHead ?? StreamId.Zero);
            connection.Send(GatewayProtocol.IdFrame);
            connection.Send([GatewayProtocol.Ping]);
        }
        else
        {
            connection.Send(GatewayProtocol.IdFrame);
            connection.Send([GatewayProtocol.Ping]);
            await RecordAsync("handshake", playerId, () => GatewayPresence.ConnectedAsync(redis, info, time.GetUtcNow()));
        }

        // After the claim (realtime:conn names this connection now), so the one it replaces closes as not current.
        _current.AddOrUpdate(playerId, connection, (_, old) =>
        {
            old.Close(WebSocketCloseStatus.NormalClosure, "replaced");
            return connection;
        });
        log.LogInformation("Player {Player} with IP {Ip} connected (connection {Connection}{Edge})", playerId, ip, info.Id, edge ? ", through an edge" : "");

        string reason = "closed by the game";
        bool otherSideClosed = false;
        try
        {
            while (await ReceiveAsync(socket, stopping) is { } message)
            {
                // The game's answer to the ping; it sends nothing else after its first frame.
                if (message.Length == 1 && message[0] == GatewayProtocol.Pong && TryGet(playerId, out var current) && current == connection)
                {
                    connection.LastAnswerMs = time.GetUtcNow().ToUnixTimeMilliseconds();
                    await RecordAsync("ping answer", playerId, () => GatewayPresence.AnsweredAsync(redis, info, time.GetUtcNow()));
                }
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException)
        {
            reason = stopping.IsCancellationRequested ? "this node is stopping" : "dropped";
        }
        finally
        {
            if (socket.State == WebSocketState.CloseReceived)
            {
                // The game closed first (on an edge's link: the edge, for the game, which closed or dropped): its close is
                // answered with its own code and reason, as the TS websocket's was (1000 when it sent none: see above).
                otherSideClosed = true;
                if (edge && (int?)socket.CloseStatus == GatewayEdge.GameDroppedCode)
                {
                    reason = "dropped, at the edge";
                }

                connection.Answer(socket.CloseStatus is { } status && status != WebSocketCloseStatus.Empty ? status : WebSocketCloseStatus.NormalClosure,
                    socket.CloseStatusDescription);
            }

            _current.TryRemove(new KeyValuePair<string, GatewayConnection>(playerId, connection));
            connection.Complete();
            await writer;
        }

        // A detached edge link is let go of as of when it ended, not when the grace ran out.
        var ended = time.GetUtcNow();
        bool reaped = edge && !otherSideClosed && !connection.ClosedGame;
        if (reaped && !await DetachedAsync(connection, stopping))
        {
            return;
        }

        bool wasCurrent = false;
        await RecordAsync("close", playerId, async () => wasCurrent = await GatewayPresence.ClosedAsync(redis, info, ended, reaped));
        log.LogInformation("Player {Player} disconnected ({Reason}; connection {Connection}{Replaced})", playerId, reaped ? "the edge's link never came back" : reason,
            info.Id, wasCurrent ? "" : ", already replaced");
    }

    // An edge's link that ended without the edge's close and without this node closing the game: the edge let go of this
    // node (to move the game to another) or died. The game is let go of only after Gateway:EdgeDetachGraceMs, and then
    // only if no other node has taken it (the release is the socket's own: GatewayPresence.ClosedAsync); true when it is
    // to be let go of now. A node that stops leaves it for a resume elsewhere (or the reaper, once this node is gone).
    private async Task<bool> DetachedAsync(GatewayConnection connection, CancellationToken stopping)
    {
        var info = connection.Info;
        if (stopping.IsCancellationRequested)
        {
            log.LogInformation("Player {Player}'s edge link let go of as this node stops (connection {Connection}): left for another node", info.PlayerId, info.Id);
            return false;
        }

        int grace = settings.CurrentValue.EdgeDetachGraceMs;
        log.LogInformation("Player {Player}'s edge link ended without a close (connection {Connection}): let go of in {Ms} ms unless another node takes it",
            info.PlayerId, info.Id, grace);
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(grace), time, stopping);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    // One frame on a socket nothing else is writing to yet (the handshake's).
    private static async Task SendAsync(WebSocket socket, byte[] frame)
    {
        using var wait = new CancellationTokenSource(s_closeWait);
        try
        {
            await socket.SendAsync(frame, WebSocketMessageType.Binary, endOfMessage: true, wait.Token);
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException)
        {
        }
    }

    // A Redis failure is logged and the connection carries on, as the TS websocket's writes were (not awaited, logged).
    private async Task RecordAsync(string what, string playerId, Func<Task> write)
    {
        try
        {
            await write();
        }
        catch (Exception e) when (e is RedisException or TimeoutException)
        {
            log.LogError("Presence for player {Player} at the {What}: {Error}", playerId, what, e.Message);
        }
    }

    // One whole message (text or binary, as the TS server took either), or null once the other side closes.
    private static async Task<byte[]?> ReceiveAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[4096];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            message.Write(buffer, 0, result.Count);
            if (message.Length > MaxMessageBytes)
            {
                throw new WebSocketException(WebSocketError.Faulted, $"a message over {MaxMessageBytes} bytes");
            }

            if (result.EndOfMessage)
            {
                return message.ToArray();
            }
        }
    }

    // Starts the close handshake and waits a moment for the other side's answer, so the close reaches it.
    private static async Task CloseAsync(WebSocket socket, WebSocketCloseStatus status, string? reason)
    {
        using var wait = new CancellationTokenSource(s_closeWait);
        try
        {
            await socket.CloseOutputAsync(status, reason, wait.Token);
            while (await ReceiveAsync(socket, wait.Token) is not null)
            {
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException)
        {
        }
    }
}

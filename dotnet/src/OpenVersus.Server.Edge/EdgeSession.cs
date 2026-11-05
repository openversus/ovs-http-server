using System.Net;
using System.Net.WebSockets;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using OpenVersus.Server.Core.Hosting;
using OpenVersus.Server.Core.Realtime;
using StackExchange.Redis;

namespace OpenVersus.Server.Edge;

/// <summary>The games this edge holds, one <see cref="EdgeSession"/> each.</summary>
internal sealed class EdgeGames(EdgeNodes nodes, IOptionsMonitor<EdgeSettings> settings, IOptionsMonitor<GatewaySettings> gateway, ServiceInstance instance,
    ILogger<EdgeSession> log)
{
    private int _count;

    /// <summary>How many games this edge holds now.</summary>
    public int Count => Volatile.Read(ref _count);

    public async Task HandleAsync(HttpContext context)
    {
        // The address the node records for the player, by the rule every service uses: passed on as X-Real-IP.
        string ip = ClientAddress.Of(context);
        using var game = await context.WebSockets.AcceptWebSocketAsync();
        Interlocked.Increment(ref _count);
        try
        {
            await new EdgeSession(game, ip, nodes, settings.CurrentValue, gateway.CurrentValue.EdgeSecret ?? "", instance.Id, log).RunAsync();
        }
        finally
        {
            Interlocked.Decrement(ref _count);
        }
    }
}

/// <summary>
/// One game, from its socket's handshake to its close: a link to a gateway node (GatewayEdge), with the game's frames
/// forwarded to it as they are and the node's taken out of their envelope. The game is closed only when the node says so
/// (or there is no node for it); the game's own close, or its socket dropping, is passed on to the node as the session's
/// end. Everything is in memory: an edge that dies takes its games' sockets with it, and there is nothing to resume.
/// </summary>
internal sealed class EdgeSession(WebSocket game, string ip, EdgeNodes nodes, EdgeSettings settings, string secret, string edgeInstance, ILogger log)
{
    // The game's first frame holds its session token (about 1 KB); a node's frames are whatever the game is sent.
    private const int MaxGameMessageBytes = 64 * 1024;
    private const int MaxNodeMessageBytes = 16 * 1024 * 1024;
    private const int QueueLimit = 4096;
    private static readonly TimeSpan s_closeWait = TimeSpan.FromSeconds(5);

    private readonly string _id = Guid.NewGuid().ToString("N");
    private readonly Channel<ToGame> _toGame = Channel.CreateBounded<ToGame>(new BoundedChannelOptions(QueueLimit)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait,
    });

    private readonly TaskCompletionSource<GameEnd> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ClientWebSocket? _link;
    private int _closing;
    private byte[] _first = [];
    private WebSocketMessageType _firstType;

    // The last entry of the player's replay log the game was sent (or the head of the log when the first link was made):
    // what a resume asks the next node to send after. Null until a node gave one.
    private StreamId? _cursor;

    // The game got its id frame: from then on a new node resumes its session (with the cursor); before, a new node takes
    // it as new (the position and the id frame come again). Also when the edge may ping it itself.
    private bool _handshaken;

    // While the game has no node: since when (Environment.TickCount64), and the edge's own pings to it.
    private long? _detachedAt;
    private CancellationTokenSource? _pinging;

    // A frame for the game, or (Frame null) its close: Code 0 drops it with no close handshake.
    private sealed record ToGame(byte[]? Frame, int Code, string? Reason);

    // How the game left: its close (its code and reason), or its socket dropping.
    private sealed record GameEnd(WebSocketCloseStatus Status, string? Reason, bool Dropped);

    private enum Outcome
    {
        /// <summary>The game left; the node was told.</summary>
        Ended,

        /// <summary>The node closed the game.</summary>
        Closed,

        /// <summary>The node refused the link as an edge's: the secret or the id (a configuration error).</summary>
        Misconfigured,

        /// <summary>No link was made, or the node did not speak the edge's protocol.</summary>
        Failed,

        /// <summary>The link ended without the node closing the game.</summary>
        Detached,
    }

    public async Task RunAsync()
    {
        using (var handshake = new CancellationTokenSource(settings.HandshakeTimeoutMs))
        {
            try
            {
                var (first, type) = await ReceiveAsync(game, MaxGameMessageBytes, handshake.Token);
                if (first is null)
                {
                    return;
                }

                (_first, _firstType) = (first, type);
            }
            catch (OperationCanceledException)
            {
                log.LogWarning("The websocket from {Ip} sent no first frame within {Ms} ms; closed", ip, settings.HandshakeTimeoutMs);
                game.Abort();
                return;
            }
            catch (WebSocketException)
            {
                return;
            }
        }

        var writer = WriteGameAsync();
        var reader = ReadGameAsync();
        await LinkAsync();

        // The game is closed or closing, and no link is left: send what is queued, then wait a moment for its close.
        _toGame.Writer.TryComplete();
        await writer;
        if (await Task.WhenAny(reader, Task.Delay(s_closeWait)) != reader)
        {
            game.Abort();
            await reader;
        }
    }

    // Links the game to a node, and to another each time its node goes (a resume: the new node sends what the game
    // missed), until the game leaves or is closed. Nodes that went or failed are tried again only when there is no other;
    // a game that has had no node for Edge:GiveUpMs is closed. A game that never had a node is closed at once when there
    // is none.
    private async Task LinkAsync()
    {
        var excluded = new HashSet<string>();
        try
        {
            while (!_ended.Task.IsCompleted)
            {
                EdgeNode? node;
                try
                {
                    node = await nodes.PickAsync(excluded);
                }
                catch (Exception e) when (e is RedisException or TimeoutException)
                {
                    // The registry could not be read: no node this try (a game in a gap keeps trying).
                    log.LogWarning("Gateway nodes not found for the game from {Ip} (connection {Connection}): {Error}", ip, _id, e.Message);
                    node = null;
                }

                if (node is null && _detachedAt is null && !_handshaken)
                {
                    log.LogWarning("No gateway node to take the game from {Ip} (connection {Connection}); closed", ip, _id);
                    CloseGame((int)WebSocketCloseStatus.EndpointUnavailable, "going away");
                    return;
                }

                var outcome = node is null ? Outcome.Failed : await AttachAsync(node);
                switch (outcome)
                {
                    case Outcome.Ended or Outcome.Closed:
                        return;
                    case Outcome.Misconfigured:
                        CloseGame((int)WebSocketCloseStatus.InternalServerError, "edge misconfigured");
                        return;
                    case Outcome.Detached:
                        // A new gap: the node just lost is the one not to try first.
                        excluded.Clear();
                        Detach("lost its node", node!);
                        break;
                    default:
                        Detach("could not be linked", node);
                        break;
                }

                if (node is not null)
                {
                    excluded.Add(node.Instance);
                    nodes.Failed(node.Instance);
                }

                if (Environment.TickCount64 - _detachedAt >= settings.GiveUpMs)
                {
                    log.LogWarning("The game from {Ip} (connection {Connection}) has had no gateway node for {Ms} ms; closed", ip, _id, settings.GiveUpMs);
                    CloseGame((int)WebSocketCloseStatus.EndpointUnavailable, "going away");
                    return;
                }

                // A node just lost: another is tried at once; after a try that failed, a moment first.
                if (outcome != Outcome.Detached)
                {
                    await Task.WhenAny(Task.Delay(RetryDelay), _ended.Task);
                }
            }
        }
        finally
        {
            StopPinging();
        }
    }

    // How long between two tries to link a game that has no node.
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);

    // The game has no node (from the first failure on): the gap's start, and the edge pings the game itself, at once
    // and then every Edge:DetachedPingMs, so its ~30 s without a ping never runs out (only once it has its id frame).
    private void Detach(string what, EdgeNode? node)
    {
        if (_detachedAt is not null)
        {
            return;
        }

        _detachedAt = Environment.TickCount64;
        log.LogInformation("The game from {Ip} (connection {Connection}) {What} {Node}: moving it", ip, _id, what, node?.Instance ?? "(none ready)");
        if (_handshaken)
        {
            _pinging = new CancellationTokenSource();
            _ = PingAsync(_pinging.Token);
        }
    }

    private async Task PingAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                SendGame([GatewayProtocol.Ping]);
                await Task.Delay(settings.DetachedPingMs, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // The game has a node again.
    private void StopPinging()
    {
        _pinging?.Cancel();
        _pinging?.Dispose();
        _pinging = null;
        _detachedAt = null;
    }

    // One link to a node, from its upgrade to its end: a resume after the cursor once the game has its id frame, else new.
    private async Task<Outcome> AttachAsync(EdgeNode node)
    {
        StreamId? resumeAfter = _handshaken ? _cursor : null;
        using var link = new ClientWebSocket();
        link.Options.KeepAliveInterval = TimeSpan.FromMilliseconds(settings.KeepAliveMs);
        link.Options.KeepAliveTimeout = TimeSpan.FromMilliseconds(settings.KeepAliveTimeoutMs);
        link.Options.CollectHttpResponseDetails = true;
        link.Options.SetRequestHeader(GatewayEdge.SecretHeader, secret);
        link.Options.SetRequestHeader(GatewayEdge.ConnectionIdHeader, _id);
        link.Options.SetRequestHeader(GatewayEdge.EdgeInstanceHeader, edgeInstance);
        if (ip.Length > 0)
        {
            link.Options.SetRequestHeader("X-Real-IP", ip);
        }

        if (resumeAfter is { } after)
        {
            link.Options.SetRequestHeader(GatewayEdge.ResumeAfterHeader, after.ToString());
        }

        try
        {
            using var connect = new CancellationTokenSource(settings.ConnectTimeoutMs);
            await link.ConnectAsync(new Uri(node.Address), connect.Token);
            await link.SendAsync(_first, _firstType, endOfMessage: true, connect.Token);
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or HttpRequestException)
        {
            if (link.HttpStatusCode is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)
            {
                log.LogError("Gateway node {Node} refused this edge's link ({Status}): Gateway:EdgeSecret differs between them, or the link is malformed",
                    node.Instance, (int)link.HttpStatusCode);
                return Outcome.Misconfigured;
            }

            log.LogWarning("Could not link the game from {Ip} to gateway node {Node} at {Address}: {Error}", ip, node.Instance, node.Address, e.Message);
            return Outcome.Failed;
        }

        Volatile.Write(ref _link, link);
        StopPinging();
        log.LogInformation("Game from {Ip} {How} gateway node {Node} (connection {Connection})", ip, resumeAfter is { } from ? $"resumed after {from} on" : "linked to",
            node.Instance, _id);
        if (_ended.Task.IsCompleted)
        {
            // The game left while the link was being made: the node is told as the reader would have told it.
            await EndLinkAsync(link, await _ended.Task);
            Volatile.Write(ref _link, null);
            return Outcome.Ended;
        }

        (int Code, string? Reason)? close = null;
        bool closedByNode = false;
        try
        {
            while (true)
            {
                var (message, _) = await ReceiveAsync(link, MaxNodeMessageBytes, CancellationToken.None);
                if (message is null)
                {
                    closedByNode = true;
                    break;
                }

                GatewayEdge.Envelope frame;
                try
                {
                    frame = GatewayEdge.Read(message);
                }
                catch (FormatException e)
                {
                    log.LogWarning("Gateway node {Node} does not speak the edge's link ({Error}): an older node? Let go of", node.Instance, e.Message);
                    Volatile.Write(ref _link, null);
                    link.Abort();
                    return Outcome.Failed;
                }

                switch (frame.Kind)
                {
                    case GatewayEdge.Kind.Unlogged:
                        _handshaken |= frame.Frame.AsSpan().SequenceEqual(GatewayProtocol.IdFrame);
                        SendGame(frame.Frame);
                        break;
                    case GatewayEdge.Kind.Logged when _cursor is not { } cursor || frame.Id > cursor:
                        _cursor = frame.Id;
                        SendGame(frame.Frame);
                        break;
                    case GatewayEdge.Kind.Position:
                        _cursor = frame.Id;
                        break;
                    case GatewayEdge.Kind.Close:
                        close = (frame.Code, frame.Reason);
                        break;
                }
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // The link dropped, or its keep-alive went unanswered.
        }

        Volatile.Write(ref _link, null);
        if (closedByNode)
        {
            await AnswerCloseAsync(link);
        }

        if (close is { } instruction)
        {
            log.LogInformation("Gateway node {Node} closed the game from {Ip} (connection {Connection}): {Code} {Reason}", node.Instance, ip, _id,
                instruction.Code, instruction.Reason);
            CloseGame(instruction.Code, instruction.Reason);
            return Outcome.Closed;
        }

        if (_ended.Task.IsCompleted)
        {
            return Outcome.Ended;
        }

        // Never a close frame on a link the edge lets go of: the node would take it for the game's end.
        link.Abort();
        return Outcome.Detached;
    }

    // The game's frames to the node it is linked to (its answers to the pings); without a link, dropped (the answers to
    // the edge's own pings). The game's close or drop is the session's end: the node is told.
    private async Task ReadGameAsync()
    {
        GameEnd end;
        try
        {
            while (true)
            {
                var (message, type) = await ReceiveAsync(game, MaxGameMessageBytes, CancellationToken.None);
                if (message is null)
                {
                    // A close with no code cannot be passed on as it is (1005 is never sent): 1000, as a node answers one.
                    end = new GameEnd(game.CloseStatus is { } status && status != WebSocketCloseStatus.Empty ? status : WebSocketCloseStatus.NormalClosure,
                        game.CloseStatusDescription, Dropped: false);
                    break;
                }

                if (Volatile.Read(ref _link) is { State: WebSocketState.Open } link)
                {
                    try
                    {
                        await link.SendAsync(message, type, endOfMessage: true, CancellationToken.None);
                    }
                    catch (Exception e) when (e is WebSocketException or ObjectDisposedException or OperationCanceledException)
                    {
                        // The link went: the session's link loop sees it.
                    }
                }
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            end = new GameEnd((WebSocketCloseStatus)GatewayEdge.GameDroppedCode, "the game dropped", Dropped: true);
        }

        _ended.TrySetResult(end);
        if (!end.Dropped)
        {
            // Its close answered (or, after the edge closed it, this was the answer: nothing more to send).
            CloseGame((int)end.Status, end.Reason);
        }

        if (Volatile.Read(ref _link) is { } current)
        {
            await EndLinkAsync(current, end);
        }
    }

    // The session ended at the game: the node is told with the game's close (or the edge's code for a drop).
    private async Task EndLinkAsync(ClientWebSocket link, GameEnd end)
    {
        try
        {
            using var wait = new CancellationTokenSource(s_closeWait);
            await link.CloseOutputAsync(end.Status, end.Reason, wait.Token);
        }
        catch (Exception e) when (e is WebSocketException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }

    // The node closed the link: its close is answered, so the node's close handshake completes.
    private static async Task AnswerCloseAsync(ClientWebSocket link)
    {
        try
        {
            using var wait = new CancellationTokenSource(s_closeWait);
            await link.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, wait.Token);
        }
        catch (Exception e) when (e is WebSocketException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }

    private void SendGame(byte[] frame)
    {
        if (Volatile.Read(ref _closing) == 0 && !_toGame.Writer.TryWrite(new ToGame(frame, 0, null)))
        {
            log.LogWarning("The game from {Ip} (connection {Connection}) stopped reading its messages; dropped", ip, _id);
            game.Abort();
        }
    }

    // Closes the game after what is queued (code 0: drops it); once, whoever asks first.
    private void CloseGame(int code, string? reason)
    {
        if (Interlocked.Exchange(ref _closing, 1) == 0 && !_toGame.Writer.TryWrite(new ToGame(null, code, reason)))
        {
            game.Abort();
        }
    }

    private async Task WriteGameAsync()
    {
        try
        {
            await foreach (var item in _toGame.Reader.ReadAllAsync())
            {
                if (item.Frame is { } frame)
                {
                    await game.SendAsync(frame, WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None);
                    continue;
                }

                if (item.Code == 0)
                {
                    game.Abort();
                }
                else if (game.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    await game.CloseOutputAsync((WebSocketCloseStatus)item.Code, item.Reason, CancellationToken.None);
                }

                return;
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // The game went first: its reader sees it.
        }
    }

    // One whole message and its type, or null once the other side closes.
    private static async Task<(byte[]? Message, WebSocketMessageType Type)> ReceiveAsync(WebSocket socket, int limit, CancellationToken ct)
    {
        var buffer = new byte[4096];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return (null, result.MessageType);
            }

            message.Write(buffer, 0, result.Count);
            if (message.Length > limit)
            {
                throw new WebSocketException(WebSocketError.Faulted, $"a message over {limit} bytes");
            }

            if (result.EndOfMessage)
            {
                return (message.ToArray(), result.MessageType);
            }
        }
    }
}

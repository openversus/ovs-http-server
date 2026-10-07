using System.Net.WebSockets;
using System.Threading.Channels;
using OpenVersus.Server.Core.Realtime;

namespace OpenVersus.Server.Realtime;

/// <summary>
/// One game's socket on this node. Everything sent to it goes through one queue and one writer (a websocket takes one
/// send at a time): the id frame, pings, delivered messages, and a close. A game that stops reading until the queue is
/// full is cut off rather than held in memory.
/// <para>
/// On an edge's link (<paramref name="edge"/>) each frame goes in the edge's envelope (<see cref="GatewayEdge"/>), and
/// closing the game is an instruction to the edge followed by the link's normal close: any other end of the link reads,
/// at the edge, as this node letting go of the game, which the edge then attaches to another node.
/// </para>
/// </summary>
internal sealed class GatewayConnection(WebSocket socket, GatewayConnectionInfo info, long connectedMs, bool edge = false)
{
    private const int QueueLimit = 4096;

    private readonly Channel<Outgoing> _out = Channel.CreateBounded<Outgoing>(new BoundedChannelOptions(QueueLimit)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait,
    });

    private long _lastAnswerMs = connectedMs;
    private int _closing;
    private volatile bool _closedGame;

    private sealed record Outgoing(byte[]? Message, WebSocketCloseStatus Status, string? Reason);

    public GatewayConnectionInfo Info { get; } = info;

    /// <summary>Whether the socket is an edge's link rather than the game's own.</summary>
    public bool Edge => edge;

    /// <summary>This node closed the game (<see cref="Close"/>, <see cref="Abort"/>): the session ends here.</summary>
    public bool ClosedGame => _closedGame;

    /// <summary>When the game last answered a ping (ms; the handshake counts as one).</summary>
    public long LastAnswerMs
    {
        get => Interlocked.Read(ref _lastAnswerMs);
        set => Interlocked.Exchange(ref _lastAnswerMs, value);
    }

    /// <summary>Queues a frame for the game; false when the game has stopped reading (the queue is full) and was cut off.</summary>
    public bool Send(byte[] frame) => Queue(edge ? GatewayEdge.Unlogged(frame) : frame);

    /// <summary>
    /// Queues a delivered message: on an edge's link with its entry in the player's replay log (<paramref name="logged"/>,
    /// from ws:send's seqs), so the edge knows how far the game got; as <see cref="Send"/> otherwise.
    /// </summary>
    public bool Deliver(byte[] message, StreamId? logged) =>
        edge && logged is { } id ? Queue(GatewayEdge.Logged(id, message)) : Send(message);

    /// <summary>On an edge's link, the head of the player's replay log at the claim: queued first, before the id frame.</summary>
    public bool SendPosition(StreamId head) => !edge || Queue(GatewayEdge.Position(head));

    /// <summary>Closes the game with a close handshake after what is already queued; once, whoever asks first.</summary>
    public void Close(WebSocketCloseStatus status, string? reason) => CloseGame((int)status, status, reason);

    /// <summary>
    /// Drops the game at once, no close handshake (the TS websocket's terminate()); on an edge's link, tells the edge to,
    /// unless a close is already on its way (then the link itself is dropped, as a direct socket would be).
    /// </summary>
    public void Abort()
    {
        if (edge && Volatile.Read(ref _closing) == 0)
        {
            CloseGame(0, WebSocketCloseStatus.NormalClosure, null);
            return;
        }

        _closedGame = true;
        Drop();
    }

    /// <summary>Answers the other side's close with its code (the game's, or on an edge's link the edge's: the session ended).</summary>
    public void Answer(WebSocketCloseStatus status, string? reason)
    {
        if (Interlocked.Exchange(ref _closing, 1) == 0 && !_out.Writer.TryWrite(new Outgoing(null, status, reason)))
        {
            Drop();
        }
    }

    /// <summary>Ends the socket at once (a game, or an edge, that stopped reading: the queue is full).</summary>
    public void Drop()
    {
        Interlocked.Exchange(ref _closing, 1);
        socket.Abort();
    }

    // Closing the game: the close itself, or on an edge's link the instruction (code 0: drop it), then the link's normal
    // close. Once, whoever asks first.
    private void CloseGame(int code, WebSocketCloseStatus status, string? reason)
    {
        if (Interlocked.Exchange(ref _closing, 1) != 0)
        {
            return;
        }

        _closedGame = true;
        bool queued = edge
            ? _out.Writer.TryWrite(new Outgoing(GatewayEdge.Close(code, reason), default, null)) && _out.Writer.TryWrite(new Outgoing(null, WebSocketCloseStatus.NormalClosure, null))
            : _out.Writer.TryWrite(new Outgoing(null, status, reason));
        if (!queued)
        {
            Drop();
        }
    }

    private bool Queue(byte[] bytes)
    {
        if (Volatile.Read(ref _closing) != 0)
        {
            return true;
        }

        if (_out.Writer.TryWrite(new Outgoing(bytes, default, null)))
        {
            return true;
        }

        Drop();
        return false;
    }

    /// <summary>Sends what is queued until the connection closes or <see cref="Complete"/>.</summary>
    public async Task WriteAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var item in _out.Reader.ReadAllAsync(ct))
            {
                if (item.Message is { } bytes)
                {
                    await socket.SendAsync(bytes, WebSocketMessageType.Binary, endOfMessage: true, ct);
                    continue;
                }

                await socket.CloseOutputAsync(item.Status, item.Reason, ct);
                return;
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // The socket went first: whoever reads it sees the close.
        }
    }

    /// <summary>Nothing more will be sent (the socket closed).</summary>
    public void Complete() => _out.Writer.TryComplete();
}

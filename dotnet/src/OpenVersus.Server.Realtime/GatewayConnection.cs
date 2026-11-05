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

    // While a resume replays the player's log: what ws:send and ws:disconnect bring meanwhile, in order (EndReplay).
    private readonly Lock _gate = new();
    private List<Held>? _held;

    // On an edge's link, the highest entry of the player's log queued here (the position at a new link, the replay's
    // last, or a delivery): a delivery at or below it was sent already (ws:send can reach this node after the replay
    // read the same entry from the log), and is dropped.
    private StreamId _lastLogged;

    private sealed record Outgoing(byte[]? Message, WebSocketCloseStatus Status, string? Reason);

    private sealed record Held(byte[]? Message, StreamId? Seq, bool Close, int? Code, string? Reason);

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
    /// from ws:send's seqs), so the edge knows how far the game got; as <see cref="Send"/> otherwise. Held while the log
    /// is being replayed.
    /// </summary>
    public bool Deliver(byte[] message, StreamId? logged)
    {
        lock (_gate)
        {
            if (_held is not null)
            {
                _held.Add(new Held(message, logged, false, null, null));
                return true;
            }

            return DeliverNow(message, logged);
        }
    }

    /// <summary>
    /// A ws:disconnect for this connection: closes the game with <paramref name="code"/> and <paramref name="reason"/>,
    /// or drops it without a code. Held while the log is being replayed.
    /// </summary>
    public void Disconnect(int? code, string? reason, StreamId? logged)
    {
        lock (_gate)
        {
            if (_held is not null)
            {
                _held.Add(new Held(null, logged, true, code, reason));
                return;
            }
        }

        CloseFor(code, reason);
    }

    /// <summary>Closes the game as a ws:disconnect asks: with its code and reason, or dropped without a code.</summary>
    public void CloseFor(int? code, string? reason)
    {
        if (code is { } status)
        {
            Close((WebSocketCloseStatus)status, reason);
        }
        else
        {
            Abort();
        }
    }

    /// <summary>
    /// A resume is replaying the player's log after <paramref name="after"/> (the last entry the game received): what is
    /// delivered from now on is held until <see cref="EndReplay"/>.
    /// </summary>
    public void BeginReplay(StreamId after)
    {
        lock (_gate)
        {
            _held = [];
            _lastLogged = after;
        }
    }

    /// <summary>One entry of the player's log, replayed: queued at once, ahead of what is held.</summary>
    public bool Replay(byte[] message, StreamId id)
    {
        lock (_gate)
        {
            _lastLogged = id;
        }

        return Queue(edge ? GatewayEdge.Logged(id, message) : message);
    }

    /// <summary>
    /// The replay is done, up to <paramref name="replayed"/>: what was held is queued in order, but for what the replay
    /// already sent (an entry at or below it, delivered live while the log was being read); then delivery is live again.
    /// </summary>
    public void EndReplay(StreamId replayed)
    {
        lock (_gate)
        {
            var held = _held ?? [];
            _held = null;
            foreach (var item in held)
            {
                if (item.Seq is { } seq && seq <= replayed)
                {
                    continue;
                }

                if (item.Close)
                {
                    CloseFor(item.Code, item.Reason);
                }
                else
                {
                    DeliverNow(item.Message!, item.Seq);
                }
            }
        }
    }

    // Under _gate.
    private bool DeliverNow(byte[] message, StreamId? logged)
    {
        if (!edge || logged is not { } id)
        {
            return Send(message);
        }

        if (id <= _lastLogged)
        {
            return true;
        }

        _lastLogged = id;
        return Queue(GatewayEdge.Logged(id, message));
    }

    /// <summary>On an edge's link, the head of the player's replay log at the claim: queued first, before the id frame.</summary>
    public bool SendPosition(StreamId head)
    {
        if (!edge)
        {
            return true;
        }

        lock (_gate)
        {
            _lastLogged = head;
        }

        return Queue(GatewayEdge.Position(head));
    }

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

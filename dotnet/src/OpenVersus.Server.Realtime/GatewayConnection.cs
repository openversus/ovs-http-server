using System.Net.WebSockets;
using System.Threading.Channels;
using OpenVersus.Server.Core.Realtime;

namespace OpenVersus.Server.Realtime;

/// <summary>
/// One game's socket on this node. Everything sent to it goes through one queue and one writer (a websocket takes one
/// send at a time): the id frame, pings, delivered messages, and a close. A game that stops reading until the queue is
/// full is cut off rather than held in memory.
/// </summary>
internal sealed class GatewayConnection(WebSocket socket, GatewayConnectionInfo info, long connectedMs)
{
    private const int QueueLimit = 4096;

    private readonly Channel<Outgoing> _out = Channel.CreateBounded<Outgoing>(new BoundedChannelOptions(QueueLimit)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait,
    });

    private long _lastAnswerMs = connectedMs;
    private int _closing;

    private sealed record Outgoing(byte[]? Message, WebSocketCloseStatus Status, string? Reason);

    public GatewayConnectionInfo Info { get; } = info;

    /// <summary>When the game last answered a ping (ms; the handshake counts as one).</summary>
    public long LastAnswerMs
    {
        get => Interlocked.Read(ref _lastAnswerMs);
        set => Interlocked.Exchange(ref _lastAnswerMs, value);
    }

    /// <summary>Queues a message; false when the game has stopped reading (the queue is full) and was cut off.</summary>
    public bool Send(byte[] message)
    {
        if (Volatile.Read(ref _closing) != 0)
        {
            return true;
        }

        if (_out.Writer.TryWrite(new Outgoing(message, default, null)))
        {
            return true;
        }

        Abort();
        return false;
    }

    /// <summary>Closes with a close handshake after what is already queued; once, whoever asks first.</summary>
    public void Close(WebSocketCloseStatus status, string? reason)
    {
        if (Interlocked.Exchange(ref _closing, 1) != 0)
        {
            return;
        }

        if (!_out.Writer.TryWrite(new Outgoing(null, status, reason)))
        {
            Abort();
        }
    }

    /// <summary>Drops the connection at once, no close handshake (the TS websocket's terminate()).</summary>
    public void Abort()
    {
        Interlocked.Exchange(ref _closing, 1);
        socket.Abort();
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

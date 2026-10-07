using System.Buffers.Binary;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text;

namespace OpenVersus.Server.Core.Realtime;

/// <summary>
/// The game's websocket as the TS websocket speaks it (src/websocket.ts; docs/REALTIME.md "The connection"): the game's
/// first frame carries its session token; the server answers with a fixed id frame and pings every 20 s with one byte,
/// which the game answers with another. Everything else the server sends is a Hydra message
/// (<c>HydraEncoder.Encode(message, webSocket: true)</c>); the game sends nothing else.
/// </summary>
public static class GatewayProtocol
{
    /// <summary>The server's ping, sent as a binary message (not a websocket control frame).</summary>
    public const byte Ping = 0x0C;

    /// <summary>The game's answer to <see cref="Ping"/>.</summary>
    public const byte Pong = 0x0A;

    /// <summary>The server's answer to the first frame: 09 01 00 24 and a 36-character id, the same for every player.</summary>
    public static readonly byte[] IdFrame = [0x09, 0x01, 0x00, 0x24, .. Encoding.ASCII.GetBytes("954e7760-539b-436c-a57d-b5623f67a74d")];

    // Where the token's length (u16, big-endian) and the token sit in the first frame. What the 0x13 bytes before them,
    // the 12-byte id after the token and the Hydra map after that mean is not known; the TS server reads only the token.
    private const int LengthAt = 0x13;
    private const int TokenAt = 0x15;

    /// <summary>The session token in the game's first frame; <see cref="FormatException"/> when the frame cannot hold one.</summary>
    public static string TokenOf(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < TokenAt)
        {
            throw new FormatException($"the first frame has {frame.Length} bytes, too few to hold a token");
        }

        int length = BinaryPrimitives.ReadUInt16BigEndian(frame[LengthAt..]);
        if (frame.Length < TokenAt + length)
        {
            throw new FormatException($"the first frame's token is {length} bytes long and only {frame.Length - TokenAt} follow");
        }

        return Encoding.UTF8.GetString(frame.Slice(TokenAt, length));
    }
}

/// <summary>The gateway's timing (the realtime executable).</summary>
public sealed class GatewaySettings
{
    // At most 25 s: the game drops a connection it has not been pinged on for about 30 s.
    [Description("How often each connection is pinged, in ms. The game answers every ping, and goes back to its title screen when it has not been pinged for about 30 s, hence at most 25,000.")]
    [Range(100, 25000)]
    public int PingIntervalMs { get; set; } = 20000;

    [Description("A connection whose game has not answered a ping for this long (ms) is closed, at the next ping round: about 80 s after the last answer with the defaults, as the TS websocket.")]
    [Range(1000, 600000)]
    public int SilenceCutoffMs { get; set; } = 61000;

    [Description("A connection that has not sent its first frame (the game's session token) within this time (ms) is closed.")]
    [Range(1000, 600000)]
    public int HandshakeTimeoutMs { get; set; } = 30000;

    [Description("The players of a gateway node that is gone (stopped, or no heartbeat in the instance registry) are taken offline by the other nodes, with a disconnected event each, once their last answer to the ping is older than this (ms). A player whose node is up is that node's to close (SilenceCutoffMs).")]
    [Range(1000, 600000)]
    public int ReapAfterMs { get; set; } = 30000;

    [Description("How often each node looks for the players of gateway nodes that are gone (ms).")]
    [Range(100, 60000)]
    public int ReapIntervalMs { get; set; } = 5000;
}

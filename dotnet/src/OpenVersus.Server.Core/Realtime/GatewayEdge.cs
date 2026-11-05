using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace OpenVersus.Server.Core.Realtime;

/// <summary>
/// The link between an edge and a gateway node (slice 3f; docs/REALTIME.md): the edge holds the game's socket and opens
/// one websocket to a node per game, naming itself with headers on the upgrade. The game's frames go to the node as they
/// are. What the node sends the edge is wrapped in an envelope (<see cref="Kind"/>) the edge takes off before the game
/// sees the frame, so the game gets exactly the bytes a direct connection would. Both ends use this codec.
/// <para>
/// The game is closed only on an explicit <see cref="Kind.Close"/>; any other end of the link is a detach, after which
/// the edge attaches the game to another node. The other way round, the edge's close frame on the link means the session
/// ended (the game closed, with its code, or dropped, <see cref="GameDroppedCode"/>), and a link that ends without one is
/// a detach.
/// </para>
/// </summary>
public static class GatewayEdge
{
    /// <summary>The shared secret (Gateway:EdgeSecret): the node trusts the other headers only with it.</summary>
    public const string SecretHeader = "X-OVS-Edge";

    /// <summary>The connection's id, minted by the edge.</summary>
    public const string ConnectionIdHeader = "X-OVS-Connection-Id";

    /// <summary>A resume: the last stream id the game received (<see cref="StreamId"/>).</summary>
    public const string ResumeAfterHeader = "X-OVS-Resume-After";

    /// <summary>The edge's own instance id (as the registry names it): recorded with the connection, for the operators.</summary>
    public const string EdgeInstanceHeader = "X-OVS-Edge-Instance";

    /// <summary>The edge's close code on the link when the game's socket dropped (no close from the game).</summary>
    public const int GameDroppedCode = 4999;

    /// <summary>The longest connection id a node takes (it lands in Redis hashes and events).</summary>
    public const int MaxConnectionIdLength = 64;

    /// <summary>The first byte of every frame a node sends an edge.</summary>
    public enum Kind : byte
    {
        /// <summary>A frame for the game that is in no log (the id frame, a ping, a matchmaking tick, an unsequenced ws:send).</summary>
        Unlogged = 0,

        /// <summary>A frame for the game from the player's replay log: its stream id (16 bytes), then the frame.</summary>
        Logged = 1,

        /// <summary>Close the game: the code (u16, big-endian; 0: drop it, no close handshake), then the reason (UTF-8).</summary>
        Close = 2,

        /// <summary>The head of the player's replay log at a new attachment (16 bytes): the edge's position until a logged frame moves it.</summary>
        Position = 3,
    }

    /// <summary>Whether <paramref name="presented"/> is the configured secret (constant time; none configured: never).</summary>
    public static bool SecretMatches(string? presented, string? secret) =>
        !string.IsNullOrEmpty(secret) && presented is not null
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(secret));

    /// <summary>An id an edge may give a connection: 1 to <see cref="MaxConnectionIdLength"/> letters, digits, '-' or '_'.</summary>
    public static bool IsConnectionId(string? id) =>
        !string.IsNullOrEmpty(id) && id.Length <= MaxConnectionIdLength && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public static byte[] Unlogged(ReadOnlySpan<byte> frame) => [(byte)Kind.Unlogged, .. frame];

    public static byte[] Logged(StreamId id, ReadOnlySpan<byte> frame)
    {
        var bytes = new byte[17 + frame.Length];
        bytes[0] = (byte)Kind.Logged;
        id.Write(bytes.AsSpan(1));
        frame.CopyTo(bytes.AsSpan(17));
        return bytes;
    }

    /// <summary>Close the game with <paramref name="code"/> and <paramref name="reason"/>; code 0: drop it.</summary>
    public static byte[] Close(int code, string? reason)
    {
        byte[] text = Encoding.UTF8.GetBytes(reason ?? "");
        var bytes = new byte[3 + text.Length];
        bytes[0] = (byte)Kind.Close;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(1), checked((ushort)code));
        text.CopyTo(bytes, 3);
        return bytes;
    }

    public static byte[] Position(StreamId head)
    {
        var bytes = new byte[17];
        bytes[0] = (byte)Kind.Position;
        head.Write(bytes.AsSpan(1));
        return bytes;
    }

    /// <summary>One frame from a node, taken apart; <see cref="FormatException"/> when it is not one.</summary>
    public static Envelope Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            throw new FormatException("an empty frame");
        }

        var kind = (Kind)bytes[0];
        return kind switch
        {
            Kind.Unlogged => new Envelope(kind, default, bytes[1..].ToArray(), 0, null),
            Kind.Logged when bytes.Length >= 17 => new Envelope(kind, StreamId.Read(bytes[1..]), bytes[17..].ToArray(), 0, null),
            Kind.Close when bytes.Length >= 3 => new Envelope(kind, default, [], BinaryPrimitives.ReadUInt16BigEndian(bytes[1..]), Encoding.UTF8.GetString(bytes[3..])),
            Kind.Position when bytes.Length == 17 => new Envelope(kind, StreamId.Read(bytes[1..]), [], 0, null),
            _ => throw new FormatException($"a frame of kind {bytes[0]} and {bytes.Length} bytes"),
        };
    }

    /// <summary>A node's frame: the game's bytes (Unlogged, Logged), a stream id (Logged, Position), a close (Close).</summary>
    public sealed record Envelope(Kind Kind, StreamId Id, byte[] Frame, int Code, string? Reason);
}

/// <summary>A Redis stream id (ms-seq): an entry's place in a player's replay log, compared as (ms, seq).</summary>
public readonly record struct StreamId(ulong Ms, ulong Seq) : IComparable<StreamId>
{
    /// <summary>Before every entry (an empty log's head).</summary>
    public static readonly StreamId Zero = default;

    public static bool TryParse(string? text, out StreamId id)
    {
        id = Zero;
        int dash = text?.IndexOf('-') ?? -1;
        if (dash <= 0 || !ulong.TryParse(text.AsSpan(0, dash), NumberStyles.None, CultureInfo.InvariantCulture, out ulong ms)
            || !ulong.TryParse(text.AsSpan(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out ulong seq))
        {
            return false;
        }

        id = new StreamId(ms, seq);
        return true;
    }

    public int CompareTo(StreamId other) => Ms != other.Ms ? Ms.CompareTo(other.Ms) : Seq.CompareTo(other.Seq);

    public static bool operator <(StreamId a, StreamId b) => a.CompareTo(b) < 0;

    public static bool operator >(StreamId a, StreamId b) => a.CompareTo(b) > 0;

    public static bool operator <=(StreamId a, StreamId b) => a.CompareTo(b) <= 0;

    public static bool operator >=(StreamId a, StreamId b) => a.CompareTo(b) >= 0;

    public override string ToString() => $"{Ms}-{Seq}";

    internal void Write(Span<byte> to)
    {
        BinaryPrimitives.WriteUInt64BigEndian(to, Ms);
        BinaryPrimitives.WriteUInt64BigEndian(to[8..], Seq);
    }

    internal static StreamId Read(ReadOnlySpan<byte> from) =>
        new(BinaryPrimitives.ReadUInt64BigEndian(from), BinaryPrimitives.ReadUInt64BigEndian(from[8..]));
}

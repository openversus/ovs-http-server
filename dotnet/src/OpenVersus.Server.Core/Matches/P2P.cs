using System.Globalization;
using System.Text.Json.Nodes;

namespace OpenVersus.Server.Core.Matches;

// P2P rollback, as the TS server's src/p2p.ts decides it: an eligible match runs on the players' own nodes
// (OVS.Rollback.Node) instead of a rollback server. The match config's host runs the engine inside its node, every other
// node forwards its game to the host over a hole-punched path, and the relay (the on-demand rollback server on the port
// the match was given) is deployed only if a node reports that no direct path opened (/ovs_p2p_failed).
//
// The notification (the stored match config) carries p2p, true or false, as every TS creator writes it; the TS
// websocket then sends a P2P match's players to 127.0.0.1 and their node's port (connections:{id} nodePort, which
// /access writes from /api/identify), and /ovs_register holds game-server-instance-ready until the host's node posts
// /ovs_p2p_ready. The switch is Rollback:P2P here and P2P_ROLLBACK in the TS server (MIGRATION-BRIDGES.md 8).

/// <summary>Which matches run P2P, and the node port a client reports.</summary>
public static class P2P
{
    /// <summary>
    /// Whether a match of <paramref name="players"/> (the notification's entries, as the TS RedisTeamEntry) runs P2P
    /// with the switch on: exactly two players that are neither a bot nor a spectator, and no spectator. As the TS
    /// isP2PEligible, bots beside the two humans do not make a match ineligible.
    /// </summary>
    public static bool IsEligible(JsonArray players)
    {
        int humans = 0, spectators = 0;
        foreach (var player in players)
        {
            if (Flag(player, "isSpectator"))
            {
                spectators++;
            }
            else if (!Flag(player, "isBot"))
            {
                humans++;
            }
        }

        return humans == 2 && spectators == 0;
    }

    /// <summary>Writes the notification's p2p (the switch and eligibility) and returns it.</summary>
    public static bool Mark(JsonObject notification, bool enabled)
    {
        bool p2p = enabled && notification["players"] is JsonArray players && IsEligible(players);
        notification["p2p"] = p2p;
        return p2p;
    }

    /// <summary>
    /// A UDP port a client reported for its node, as the TS parseNodePort: a number or a string of one to five digits
    /// (surrounding white space allowed), 1..65535; 0 for anything else.
    /// </summary>
    public static int ParseNodePort(JsonNode? value)
    {
        if (value is not JsonValue v)
        {
            return 0;
        }

        if (v.TryGetValue(out string? text))
        {
            return ParseNodePort(text);
        }

        return Number(v) is { } n && n == Math.Floor(n) && n is >= 1 and <= 65535 ? (int)n : 0;
    }

    /// <inheritdoc cref="ParseNodePort(JsonNode?)"/>
    public static int ParseNodePort(string? text)
    {
        // JavaScript's trim (white space and line terminators), then /^\d{1,5}$/ (ASCII digits only).
        string trimmed = (text ?? "").Trim();
        return trimmed.Length is >= 1 and <= 5 && trimmed.All(char.IsAsciiDigit)
            && int.Parse(trimmed, CultureInfo.InvariantCulture) is var port and >= 1 and <= 65535 ? port : 0;
    }

    // TS truthiness of a flag (only true is ever written).
    private static bool Flag(JsonNode? player, string name) => player?[name] is JsonValue v && v.GetValueKind() switch
    {
        System.Text.Json.JsonValueKind.True => true,
        System.Text.Json.JsonValueKind.Number => Number(v) is { } d && d != 0 && !double.IsNaN(d),
        System.Text.Json.JsonValueKind.String => v.GetValue<string>().Length > 0,
        _ => false,
    };

    // A number however the node holds it (parsed, or created from an int, long or double).
    private static double? Number(JsonValue v) =>
        v.GetValueKind() == System.Text.Json.JsonValueKind.Number
            ? double.Parse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture)
            : null;
}

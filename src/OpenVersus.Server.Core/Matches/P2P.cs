using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Matches;

// P2P rollback, as the TS server's src/p2p.ts decides it: an eligible match runs on the players' own nodes
// (OVS.Rollback.Node) instead of a rollback server. The match config's host runs the engine inside its node, every other
// node forwards its game to the host over a hole-punched path, and the relay (the on-demand rollback server on the port
// the match was given) is deployed only if a node reports that no direct path opened (/ovs_p2p_failed).
//
// The notification (the stored match config) carries p2p, true or false, as every TS creator writes it; the TS
// websocket then sends a P2P match's players to 127.0.0.1 and their node's port (connections:{id} nodePort, which
// /access writes from /api/identify), and /ovs_register (RollbackCallbacks) holds game-server-instance-ready until the
// host's node posts /ovs_p2p_ready. The switch is Rollback:P2P (P2P_ROLLBACK fills it when it is not set itself: TsEnvironment).
//
// Every human in the match (players and spectators: each is sent to its own node) must have reported a node, or the
// match runs on a rollback server (Tuggernuts, 2026-10-19). A game without one ([Features] RollbackNode = false, an old
// client, or a node that never reported its port) was sent to 127.0.0.1 with nothing listening, and the relay could not
// save it: the relay is asked for by a node and reached through one.

/// <summary>Which matches run P2P, and the node port a client reports.</summary>
public static class P2P
{
    /// <summary>
    /// Whether a match of <paramref name="players"/> (the notification's entries, as the TS RedisTeamEntry) runs P2P
    /// with the switch on, as the TS hasP2PHost: any mode and any players (1v1, 2v2, FFA; bots and spectators
    /// included), as long as one player is a human who plays, since the host's node runs the engine.
    /// </summary>
    public static bool IsEligible(JsonArray players) =>
        players.Any(player => !Flag(player, "isSpectator") && !Flag(player, "isBot"));

    /// <summary>
    /// The humans of <paramref name="players"/>, players and spectators (everyone but bots), by playerId; "" for an entry
    /// without one. In a P2P match each of them is sent to their own node.
    /// </summary>
    public static IEnumerable<string> Humans(JsonArray players) =>
        players.Where(player => !Flag(player, "isBot"))
            .Select(player => player?["playerId"] is JsonValue v && v.TryGetValue(out string? id) ? id : "");

    /// <summary>
    /// Writes the notification's p2p and returns it: the switch, eligibility, and every human with a node
    /// (<paramref name="hasNode"/>, asked only with the switch on and the match eligible).
    /// </summary>
    public static bool Mark(JsonObject notification, bool enabled, Func<string, bool> hasNode)
    {
        bool p2p = enabled && notification["players"] is JsonArray players && IsEligible(players) && Humans(players).All(hasNode);
        notification["p2p"] = p2p;
        return p2p;
    }

    /// <summary>
    /// <see cref="Mark(JsonObject, bool, Func{string, bool})"/> with each human's node read from Redis (connections:{id}
    /// nodePort above 0). An eligible match that runs on a rollback server for want of a node is logged with who has none.
    /// </summary>
    public static async Task<bool> MarkAsync(IDatabase redis, JsonObject notification, bool enabled, ILogger log)
    {
        var withNode = new HashSet<string>();
        var without = new List<string>();
        if (enabled && notification["players"] is JsonArray players && IsEligible(players))
        {
            foreach (string id in Humans(players).Distinct())
            {
                if (id.Length > 0 && ParseNodePort((string?)await redis.HashGetAsync($"connections:{id}", "nodePort")) > 0)
                {
                    withNode.Add(id);
                }
                else
                {
                    without.Add(id.Length > 0 ? id : "(no playerId)");
                }
            }
        }

        bool p2p = Mark(notification, enabled, withNode.Contains);
        if (without.Count > 0)
        {
            log.LogInformation("Match {Match} runs on a rollback server, not P2P: no rollback node reported by {Players}",
                (string?)notification["matchId"], string.Join(", ", without));
        }

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

using System.Globalization;
using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Compat;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Matches;

// A match's live network figures, for the website's live matches page: what each player's OpenVersus client reads off
// its own game while the match plays (its NetStats sampler: the input delay, the ping the game was told, and the mean
// depth of its rollbacks since its last report) and posts to POST /ovs/netstats (the web service) 5 s into the match
// and every 10 s after. Kept beside the match, per player, and shown while they are fresh: a client that stops
// reporting (the match ended, the game closed, an older client that never reports) simply has none.
//
//   match_netstats:{match}   hash: player id to {delay, ping, depth, at (ms), final?}; EX 60 s from the last report,
//                            10 min once it holds a final one
//
// When the match ends for a client it sends one last report, marked final: its figures for the whole match (the delay
// it ended on, its last ping, the mean depth of all its rollbacks). That one stays: it is what the page shows beside
// the player for as long as the finished match is listed (and, in a ranked set, until the next game's first report),
// and a report still on its way from before the end does not replace it.
// The page lists only the matches the ops view lists (Ops.OpsService.MatchesAsync): a custom lobby's match, and any
// other match marked isCustomGame but Testing Grounds', is not on it. A report for one of those is accepted and
// nothing is done with it (nothing kept, nothing logged): the client need not know which matches are listed, and the
// path is there for whatever is done with those later.
// A report counts only from a player of that match (the match's config lists them, not as a spectator or a bot): the
// figures are shown beside a player's name, so nobody else may write them. They are a client's own word about itself
// (as its quit report is), bounded here to what a game can show; nothing is decided from them.
// How the match is played, for the same page: over direct paths between the players' own nodes (the config's p2p), or
// over a relay: a match never given to the nodes, or one given to them that a relay carries after all. The second is
// known two ways: a node reported no direct path and was given the relay (p2p_relay:{match}, RollbackCallbacks), or a
// rollback server sent a match status event for it (match_on_relay:{match}, MatchStatusEvents): a player's node sends
// none (its FireMatchEvents is off), and a node that takes the relay its rendezvous names asks nobody, so the relay's
// own events are the only word of it the server gets.

/// <summary>
/// A player's figures: the input delay (frames), the ping (ms), the mean rollback depth (frames) since their last
/// report; or, <paramref name="Final"/>, their figures for the whole match, sent when it ended.
/// </summary>
public sealed record LiveNetStat(int Delay, int Ping, double Depth, bool Final = false);

/// <summary>What became of a report: kept; accepted and not kept (a match the page does not list: <paramref name="Refused"/> null); or refused, and why.</summary>
public sealed record NetStatsVerdict(bool Kept, string? Refused)
{
    public static readonly NetStatsVerdict WasKept = new(true, null), NotListed = new(false, null);

    public static NetStatsVerdict Refuse(string why) => new(false, why);
}

public static class LiveNetStats
{
    /// <summary>How long a match's figures are kept after its last report.</summary>
    public static readonly TimeSpan Kept = TimeSpan.FromSeconds(60);

    /// <summary>How old a player's figures may be and still be shown: two reports (10 s apart) and some. A final one does not age.</summary>
    public static readonly TimeSpan Fresh = TimeSpan.FromSeconds(25);

    /// <summary>How long a match's figures are kept once a final report is among them: longer than a finished match is listed.</summary>
    public static readonly TimeSpan FinalKept = Ops.MatchView.FinishedListedFor * 2;

    public const string P2P = "p2p", Relay = "relay", RelayAfterP2P = "relay-fallback";

    public static string Key(string matchId) => $"match_netstats:{matchId}";

    /// <summary>Set when a rollback server (a relay) sent a match status event for the match: it carries it.</summary>
    public static string OnRelayKey(string matchId) => $"match_on_relay:{matchId}";

    /// <summary>Records <paramref name="body"/> ({matchId, delay, ping, depth, final?}) as <paramref name="playerId"/>'s figures.</summary>
    public static async Task<NetStatsVerdict> ReportAsync(IDatabase redis, string playerId, JsonObject? body, DateTimeOffset now)
    {
        if (body?["matchId"] is not JsonValue id || !id.TryGetValue(out string? matchId) || matchId.Length is 0 or > 64 || !matchId.All(char.IsAsciiLetterOrDigit))
        {
            return NetStatsVerdict.Refuse("no match id");
        }

        if (Number(body["delay"]) is not { } delay || Number(body["ping"]) is not { } ping || Number(body["depth"]) is not { } depth)
        {
            return NetStatsVerdict.Refuse("delay, ping and depth must be numbers");
        }

        var config = await ConfigAsync(redis, matchId);
        var players = config?["players"] as JsonArray;
        if (players is null || !players.OfType<JsonObject>().Any(p => Text(p["playerId"]) == playerId && !RollbackCallbacks.Truthy(p["isSpectator"]) && !RollbackCallbacks.Truthy(p["isBot"])))
        {
            return NetStatsVerdict.Refuse("not a player of that match");
        }

        if (!Listed(config!))
        {
            return NetStatsVerdict.NotListed;
        }

        bool final = body["final"] is JsonValue f && f.GetValueKind() == System.Text.Json.JsonValueKind.True;
        // A report from before the end that arrives after the final one: the final one stands.
        if (!final && Parse((string?)await redis.HashGetAsync(Key(matchId), playerId)) is { } kept && RollbackCallbacks.Truthy(kept["final"]))
        {
            return NetStatsVerdict.WasKept;
        }

        // What a game can show: the delay is a byte, the ping is told as at most 255 (999 leaves room), a rollback is
        // at most the game's buffer (30 frames; 300 leaves room).
        var stat = new JsonObject
        {
            ["delay"] = (int)Math.Clamp(Math.Round(delay), 0, 255),
            ["ping"] = (int)Math.Clamp(Math.Round(ping), 0, 999),
            ["depth"] = Math.Round(Math.Clamp(depth, 0, 300), 2),
            ["at"] = now.ToUnixTimeMilliseconds(),
        };
        if (final)
        {
            stat["final"] = true;
        }

        await redis.HashSetAsync(Key(matchId), playerId, stat.ToJsonString());
        // Never shortened: another player's final report may already have given the match its longer life.
        var wanted = final ? FinalKept : Kept;
        if (await redis.KeyTimeToLiveAsync(Key(matchId)) is not { } left || left < wanted)
        {
            await redis.KeyExpireAsync(Key(matchId), wanted);
        }

        return NetStatsVerdict.WasKept;
    }

    // Whether the live matches page lists the match (as Ops.OpsService.MatchesAsync decides it): not a custom game,
    // unless it is a Testing Grounds match (unranked, so marked as one, with its queue named in the config override).
    private static bool Listed(JsonObject config) =>
        !RollbackCallbacks.Truthy(config["isCustomGame"])
        || Matchmaking.TestingGrounds.ByEventQueue(Text((config["gameplayConfigOverride"] as JsonObject)?["EventQueueSlug"])) is not null;

    /// <summary>The figures of <paramref name="matchId"/>'s players to show, by player id: a final one, else one that is fresh.</summary>
    public static async Task<IReadOnlyDictionary<string, LiveNetStat>> ReadAsync(IDatabase redis, string matchId, DateTimeOffset now)
    {
        var stats = new Dictionary<string, LiveNetStat>();
        foreach (var entry in await redis.HashGetAllAsync(Key(matchId)))
        {
            if (Parse(entry.Value.ToString()) is { } stat && Number(stat["at"]) is { } at
                && Number(stat["delay"]) is { } delay && Number(stat["ping"]) is { } ping && Number(stat["depth"]) is { } depth
                && RollbackCallbacks.Truthy(stat["final"]) is var final && (final || now.ToUnixTimeMilliseconds() - at <= Fresh.TotalMilliseconds))
            {
                stats[entry.Name.ToString()] = new LiveNetStat((int)delay, (int)ping, depth, final);
            }
        }

        return stats;
    }

    /// <summary>How <paramref name="matchId"/> is played: <see cref="P2P"/>, <see cref="Relay"/>, or <see cref="RelayAfterP2P"/> (given to the nodes, then to the relay); null when the match is not known.</summary>
    public static async Task<string?> TransportAsync(IDatabase redis, string matchId)
    {
        if (await ConfigAsync(redis, matchId) is not { } config)
        {
            return null;
        }

        if (!RollbackCallbacks.Truthy(config["p2p"]))
        {
            return Relay;
        }

        return (string?)await redis.StringGetAsync($"p2p_relay:{matchId}") is { Length: > 0 } || await redis.KeyExistsAsync(OnRelayKey(matchId)) ? RelayAfterP2P : P2P;
    }

    private static async Task<JsonObject?> ConfigAsync(IDatabase redis, string matchId) => Parse((string?)await redis.StringGetAsync(matchId));

    private static JsonObject? Parse(string? json)
    {
        try
        {
            return json is null ? null : Js.Parse(json) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    private static double? Number(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number
            && double.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && double.IsFinite(d) ? d : null;
}

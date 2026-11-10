using StackExchange.Redis;

namespace OpenVersus.Server.Core.Realtime;

// Pings for the players of an open rematch vote (Matches/Rematches.cs, "Who is still there"). The regular ping goes out
// every Gateway:PingIntervalMs (20 s), which says nothing about a game inside a 25 s vote. While a vote is open its
// players are pinged every Gateway:VotePingIntervalMs (1 s) as well, by the gateway node holding each one's connection,
// and both sides are counted: the pings sent and the answers received. The vote's check reads the two counts.
//
// The game answers each ping on its own (one pong, from its connection's update; nothing in it depends on the interval:
// read from its code, exe f97148ff, 2026-11-10), and a websocket loses no single answer: a game that stalls answers late,
// all at once. So the counts say how far behind a game is at the moment they are read, not how often it stalled.
//
//   realtime:votepings            ZSET: player -> until when (ms) they are pinged for a vote
//   realtime:votepings:{player}   hash: sent, answered (EX 2 min, from the vote's start)
//
// Written by whoever opens the vote (StartAsync: the match flow) and by the gateway nodes (SentAsync, AnsweredAsync);
// read by the vote's check (lobbies). An answer is counted whichever ping it was to, the regular one included, so the
// answers can exceed the pings by one or two.
public static class VotePings
{
    public const string ActiveKey = "realtime:votepings";

    public static string CountsKey(string playerId) => $"realtime:votepings:{playerId}";

    private static readonly TimeSpan s_countsTtl = TimeSpan.FromMinutes(2);

    /// <summary>Fewer pings than this say nothing either way (a player who connected late in the window).</summary>
    public const int MinSent = 4;

    /// <summary>A vote opened: its <paramref name="playerIds"/> are pinged until <paramref name="until"/>, counted from zero.</summary>
    public static async Task StartAsync(IDatabase redis, IEnumerable<string> playerIds, DateTimeOffset until)
    {
        foreach (string id in playerIds.Distinct())
        {
            await redis.KeyDeleteAsync(CountsKey(id));
            await redis.SortedSetAddAsync(ActiveKey, id, until.ToUnixTimeMilliseconds());
        }
    }

    /// <summary>The players being pinged for a vote at <paramref name="now"/>; those whose time is up are dropped.</summary>
    public static async Task<HashSet<string>> ActiveAsync(IDatabase redis, DateTimeOffset now)
    {
        long ms = now.ToUnixTimeMilliseconds();
        await redis.SortedSetRemoveRangeByScoreAsync(ActiveKey, double.NegativeInfinity, ms);
        return [.. (await redis.SortedSetRangeByScoreAsync(ActiveKey, ms, double.PositiveInfinity, Exclude.Start)).Select(v => v.ToString())];
    }

    /// <summary>A gateway node sent <paramref name="playerId"/> a vote ping.</summary>
    public static Task SentAsync(IDatabase redis, string playerId) => CountAsync(redis, playerId, "sent");

    /// <summary>The game of <paramref name="playerId"/> answered a ping while they are pinged for a vote.</summary>
    public static Task AnsweredAsync(IDatabase redis, string playerId) => CountAsync(redis, playerId, "answered");

    private static async Task CountAsync(IDatabase redis, string playerId, string field)
    {
        await redis.HashIncrementAsync(CountsKey(playerId), field);
        await redis.KeyExpireAsync(CountsKey(playerId), s_countsTtl);
    }

    /// <summary>The vote pings sent to <paramref name="playerId"/> and the answers counted since the vote opened.</summary>
    public static async Task<(int Sent, int Answered)> CountsAsync(IDatabase redis, string playerId)
    {
        var counts = await redis.HashGetAsync(CountsKey(playerId), ["sent", "answered"]);
        return ((int)(counts[0].IsNullOrEmpty ? 0 : (long)counts[0]), (int)(counts[1].IsNullOrEmpty ? 0 : (long)counts[1]));
    }

    /// <summary>
    /// How many of <paramref name="sent"/> pings count as missed with <paramref name="answered"/> answers in: one ping
    /// may still be on its way (sent a moment before the counts were read), and answers past the pings count for nothing.
    /// </summary>
    public static int Missed(int sent, int answered) => Math.Max(0, sent - answered - 1);

    /// <summary>
    /// Whether a game with these counts is gone: at least <see cref="MinSent"/> pings sent, and
    /// <paramref name="percent"/> of them (rounded up) or more missed.
    /// </summary>
    public static bool Gone(int sent, int answered, int percent) =>
        sent >= MinSent && Missed(sent, answered) >= (int)Math.Ceiling(sent * percent / 100.0);
}

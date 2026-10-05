using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Leaderboards;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.RewardTracks;

// Who End Game's ranked-set XP pays, for the sets and public FFA games C# settles (RankedSets, MatchStatusEvents through
// SetRatings; MatchResultStream for FFA): the rules of the TS server's rankedSetXpService.ts, published the same way, on
// reward_tracks:ranked_set, for RankedSetXpSubscriber to pay (once per setKey and player).
//
//   - XP needs at least one game played: a set that ends before any game finished (a pregame dodge in game 1) pays
//     nobody, not even the side given the win.
//   - After that, whoever quit (conceded, walked out, dodged before game 2 or 3) gets nothing; everyone else is paid,
//     the winners and a 2v2 teammate who stayed alike. A concede that names nobody counts every loser as a quitter.
//   - A public FFA game pays every human in it, the winner as a win.
//
// Published  reward_tracks:ranked_set {playerId, won, character, setKey ("ranked:{set}" or "ffa:{match}"), source}

/// <summary>Publishes End Game's ranked-set and FFA XP for <see cref="RankedSetXpSubscriber"/> to pay.</summary>
public static class RankedSetXpPayout
{
    /// <summary>The games <paramref name="outcome"/>'s set finished: the score's, or for a dodge those before it.</summary>
    public static int GamesPlayed(SetOutcome outcome) =>
        outcome.IsPregameDodge ? outcome.GamesBeforeDodge : outcome.Team0Wins + outcome.Team1Wins;

    /// <summary>Who <paramref name="outcome"/> pays, and whether they won.</summary>
    public static IReadOnlyList<(string Player, bool Won)> Recipients(SetOutcome outcome)
    {
        if (GamesPlayed(outcome) < 1)
        {
            return [];
        }

        var quitters = new HashSet<string>(outcome.QuitterIds is { Count: > 0 } named ? named : outcome.IsConcede ? outcome.LoserIds : []);
        return [.. outcome.WinnerIds.Select(id => (id, true)).Concat(outcome.LoserIds.Select(id => (id, false))).Where(r => !quitters.Contains(r.id))];
    }

    /// <summary>Publishes what a rated set pays.</summary>
    public static async Task PublishSetAsync(IDatabase redis, SetOutcome outcome)
    {
        foreach (var (player, won) in Recipients(outcome))
        {
            await PublishAsync(redis, player, won, outcome.Characters.GetValueOrDefault(player) ?? "", $"ranked:{outcome.MatchId}",
                $"ranked set {(won ? "win" : "loss")} {outcome.MatchId}");
        }
    }

    /// <summary>Publishes what a public FFA game pays one of its players.</summary>
    public static Task PublishFfaAsync(IDatabase redis, string playerId, bool won, string character, string matchId) =>
        PublishAsync(redis, playerId, won, character, $"ffa:{matchId}", $"FFA {(won ? "win" : "game")} {matchId}");

    private static Task PublishAsync(IDatabase redis, string playerId, bool won, string character, string setKey, string source) =>
        redis.PublishAsync(RedisChannel.Literal(RankedSetXpSubscriber.Channel), new JsonObject
        {
            ["playerId"] = playerId,
            ["won"] = won,
            ["character"] = character,
            ["setKey"] = setKey,
            ["source"] = source,
        }.ToJsonString());
}

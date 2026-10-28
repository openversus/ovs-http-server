using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Leaderboards;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.RewardTracks;

// Who End Game's ranked-set XP pays, for the sets and public FFA games C# settles (RankedSets, MatchStatusEvents through
// SetRatings; MatchResultStream for FFA): the rules of the TS server's rankedSetXpService.ts, appended to match:results
// (field set_xp), where the match flow's consumer hands each record to RankedSetXpPayer (paid once per setKey and player;
// a record that could not be paid yet stays pending and is paid on its retry).
//
//   - XP needs at least one game played: a set that ends before any game finished (a pregame dodge in game 1) pays
//     nobody, not even the side given the win.
//   - After that, whoever quit (conceded, walked out, dodged before game 2 or 3) gets nothing; everyone else is paid,
//     the winners and a 2v2 teammate who stayed alike. A concede that names nobody counts every loser as a quitter.
//   - A public FFA game pays every human in it, the winner as a win.
//   - A Testing Grounds game pays TestingGrounds:XpPercent of what an FFA game does, each player's first
//     TestingGrounds:DailyXpGames a day only (MatchResultStream counts them).
//
// Redis, appended  match:results set_xp {playerId, won, character, setKey ("ranked:{set}", "ffa:{match}" or
//                  "testinggrounds:{match}"), source, percent (a Testing Grounds game's, when not 100)}

/// <summary>Appends End Game's ranked-set and FFA XP to match:results, for <see cref="RankedSetXpPayer"/> to pay.</summary>
public static class RankedSetXpPayout
{
    /// <summary>The match:results field a payment record is in.</summary>
    public const string Field = "set_xp";

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

    /// <summary>Appends what a rated set pays.</summary>
    public static async Task AppendSetAsync(IDatabase redis, SetOutcome outcome)
    {
        foreach (var (player, won) in Recipients(outcome))
        {
            await AppendAsync(redis, player, won, outcome.Characters.GetValueOrDefault(player) ?? "", $"ranked:{outcome.MatchId}",
                $"ranked set {(won ? "win" : "loss")} {outcome.MatchId}");
        }
    }

    /// <summary>Appends what a public FFA game pays one of its players.</summary>
    public static Task AppendFfaAsync(IDatabase redis, string playerId, bool won, string character, string matchId) =>
        AppendAsync(redis, playerId, won, character, $"ffa:{matchId}", $"FFA {(won ? "win" : "game")} {matchId}");

    /// <summary>
    /// Appends what a Testing Grounds game pays one of its players: <paramref name="percent"/> of an FFA game's
    /// (TestingGrounds:XpPercent; the daily limit is the caller's).
    /// </summary>
    public static Task AppendTestingGroundsAsync(IDatabase redis, string playerId, bool won, string character, string matchId, int percent) =>
        AppendAsync(redis, playerId, won, character, $"testinggrounds:{matchId}", $"Testing Grounds {(won ? "win" : "game")} {matchId}", percent);

    private static Task AppendAsync(IDatabase redis, string playerId, bool won, string character, string setKey, string source, int percent = 100)
    {
        var record = new JsonObject
        {
            ["playerId"] = playerId,
            ["won"] = won,
            ["character"] = character,
            ["setKey"] = setKey,
            ["source"] = source,
        };
        if (percent != 100)
        {
            record["percent"] = percent;
        }

        return redis.StreamAddAsync(Matches.MatchResults.Stream, Field, record.ToJsonString(), maxLength: 10_000, useApproximateMaxLength: true);
    }
}

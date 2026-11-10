using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Leaderboards;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Matches;

/// <summary>
/// A ranked set's games as they were decided, kept by the set (ranked_set_games:{set}, a hash: the game's match id to
/// {winner: the team, characters: {player: fighter}, random: [the players who went in as Random]}): what its ratings
/// need once a player's fighter is not the same in every game (the Random pick: RandomFighter, SetRatings). Written when
/// a game's winner is counted (RankedSets.RecordWinnerAsync, under the set's lock; a winner decided again overwrites
/// it), from that game's own keys while they are there: match_characters:{game} lives 20 min from its config and
/// match_random:{game} 30, a set up to about 25. Kept <see cref="Ttl"/> from the last game counted, deleted with the set.
/// </summary>
public static class SetGames
{
    /// <summary>Longer than the longest wait between one game's result and the set's rating: one more game, and its check-ins.</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);

    public static string Key(string setId) => $"ranked_set_games:{setId}";

    /// <summary>Game <paramref name="matchId"/> of set <paramref name="setId"/> went to team <paramref name="winner"/>.</summary>
    public static async Task RecordAsync(IDatabase redis, string setId, string matchId, int winner)
    {
        var game = new JsonObject
        {
            ["winner"] = winner,
            ["characters"] = await JsonAsync(redis, $"match_characters:{matchId}") ?? [],
            ["random"] = new JsonArray([.. (await RandomFighter.PickersAsync(redis, matchId)).Order(StringComparer.Ordinal).Select(id => (JsonNode)id)]),
        };
        await redis.HashSetAsync(Key(setId), matchId, Js.Stringify(game));
        await redis.KeyExpireAsync(Key(setId), Ttl);
    }

    /// <summary>The players who went into a counted game of the set as Random.</summary>
    public static async Task<HashSet<string>> PickersAsync(IDatabase redis, string setId) =>
        [.. (await GamesAsync(redis, setId)).SelectMany(g => g.Random)];

    /// <summary>
    /// For a set <paramref name="winnerTeam"/> won: each player who played it as Random, with what it does to each
    /// fighter they played (SetCredits). The games are the set's counted ones, then <paramref name="currentMatchId"/>
    /// when it is not one of them: the game the set ended in before its result (a concede, a disconnect, a dodge on the
    /// perk screen), whose fighter counts as the set went. A set ended by a <paramref name="concede"/> is a 2-0 for the
    /// winners whatever its games were: every fighter counts as the set went. Null when nobody played it as Random.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, IReadOnlyList<FighterCredit>>?> RandomPickersAsync(IDatabase redis, string setId, string currentMatchId,
        IReadOnlyDictionary<string, int> teams, int winnerTeam, bool concede)
    {
        var games = await GamesAsync(redis, setId);
        (JsonObject Characters, HashSet<string> Random)? unfinished = null;
        if (currentMatchId.Length > 0 && games.All(g => g.MatchId != currentMatchId) && await JsonAsync(redis, $"match_characters:{currentMatchId}") is { } characters)
        {
            unfinished = (characters, await RandomFighter.PickersAsync(redis, currentMatchId));
        }

        var pickers = games.SelectMany(g => g.Random).Concat(unfinished?.Random ?? []).Distinct().Where(teams.ContainsKey).Order(StringComparer.Ordinal).ToList();
        if (pickers.Count == 0)
        {
            return null;
        }

        var credits = new Dictionary<string, IReadOnlyList<FighterCredit>>();
        foreach (string id in pickers)
        {
            var played = games.Select(g => (Character: Text(g.Characters[id]) ?? "", Won: concede ? null : (bool?)(g.Winner == teams[id])))
                .Append((Character: Text(unfinished?.Characters[id]) ?? "", Won: null))
                .Where(g => !RandomFighter.IsRandom(g.Character));
            credits[id] = SetCredits.Of(teams[id] == winnerTeam, played);
        }

        return credits;
    }

    // The counted games, in the order played (a match id is an ObjectId: it sorts by its time).
    private static async Task<List<(string MatchId, int Winner, JsonObject Characters, HashSet<string> Random)>> GamesAsync(IDatabase redis, string setId)
    {
        var games = new List<(string MatchId, int Winner, JsonObject Characters, HashSet<string> Random)>();
        foreach (var entry in (await redis.HashGetAllAsync(Key(setId))).OrderBy(e => e.Name.ToString(), StringComparer.Ordinal))
        {
            if (Parse(entry.Value) is { } game && game["winner"] is JsonValue winner && winner.TryGetValue(out int team))
            {
                games.Add((entry.Name.ToString(), team, game["characters"] as JsonObject ?? [],
                    [.. (game["random"] as JsonArray ?? []).Select(Text).OfType<string>()]));
            }
        }

        return games;
    }

    private static async Task<JsonObject?> JsonAsync(IDatabase redis, string key) => Parse(await redis.StringGetAsync(key));

    private static JsonObject? Parse(RedisValue value)
    {
        try
        {
            return value.HasValue ? Js.Parse(value.ToString()) as JsonObject : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonNode? value) => value is JsonValue v && v.TryGetValue(out string? s) ? s : null;
}

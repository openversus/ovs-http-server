using StackExchange.Redis;

namespace OpenVersus.Server.Core.Matches;

// The Random fighter (Tuggernuts, 2026-10-14): character select's "Random" entry (the OVS pak's character_ovs_random, the
// game's "?" art; its skin skin_ovs_random) is a pick, not a fighter. Every match config is built by
// GameplayConfigs.BuildAsync, which calls ResolveAsync first for each player: one whose match loadout (connections:{id},
// copied from the lobby pick player:{id} by PlayedLoadout when the match starts; a bot's bot_config:{id}) is the Random
// fighter gets a random real one there, in a random skin of theirs (BotFighters: the released characters and their
// skins). So the config, match_characters, stats and the live matches page all see the real fighter.
// - The lobby pick (player:{id}, Mongo) is never changed: everyone keeps seeing "?" in the lobby.
// - Every game rolls again, whatever it is a game of: a queue's match and a custom lobby's copy the lobby pick into the
//   session when they start, and the games that follow one without going through a lobby (a ranked set's next game, a
//   Casual or Testing Grounds rematch) put the session back to the pick first (RollAgainAsync). The pick is what is
//   locked for a ranked set; the fighter it becomes is new each game.
// - Who went into a match as Random is kept beside it (match_random:{match}, a set of player ids, written by the roll):
//   the next game's roll and a ranked set's ratings (SetRatings: the Random rating, and each fighter played) read it. A
//   pick is never read back from player:{id} for a match already played: it may have changed, or be gone.
// - A retried build finds the pick already resolved; random_pick:{match}:{id} (SET NX) makes two concurrent builds agree.
public static class RandomFighter
{
    /// <summary>The Random fighter's slugs (the OVS pak's CharacterData and SkinData).</summary>
    public const string Character = "character_ovs_random", Skin = "skin_ovs_random";

    private static readonly TimeSpan s_pickTtl = TimeSpan.FromMinutes(30);

    public static bool IsRandom(string? character) => string.Equals(character, Character, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The real fighter for <paramref name="playerId"/> in <paramref name="matchId"/> when their match loadout (the hash
    /// at <paramref name="loadoutKey"/>: connections:{id}, or bot_config:{id} for a bot) is the Random fighter, written
    /// there; null (nothing written) when it is not.
    /// </summary>
    public static async Task<(string Character, string Skin)?> ResolveAsync(IDatabase redis, string matchId, string playerId, string loadoutKey, Random? random = null)
    {
        if (!IsRandom(await redis.HashGetAsync(loadoutKey, "character")))
        {
            return null;
        }

        var (character, skin) = BotFighters.Pick(1, random ?? Random.Shared)[0];
        string key = $"random_pick:{matchId}:{playerId}";
        if (!await redis.StringSetAsync(key, $"{character}|{skin}", s_pickTtl, When.NotExists)
            && ((string?)await redis.StringGetAsync(key))?.Split('|') is [{ Length: > 0 } kept, { Length: > 0 } keptSkin])
        {
            (character, skin) = (kept, keptSkin);
        }

        if (loadoutKey.StartsWith("connections:", StringComparison.Ordinal))
        {
            await PlayedLoadout.RecordAsync(redis, playerId, character, skin);
            await redis.SetAddAsync(PickersKey(matchId), playerId);
            await redis.KeyExpireAsync(PickersKey(matchId), s_pickTtl);
        }
        else
        {
            await redis.HashSetAsync(loadoutKey, [new HashEntry("character", character), new HashEntry("skin", skin)]);
        }

        return (character, skin);
    }

    /// <summary>The players (never bots) who went into <paramref name="matchId"/> as Random: match_random:{match}.</summary>
    public static string PickersKey(string matchId) => $"match_random:{matchId}";

    /// <inheritdoc cref="PickersKey"/>
    public static async Task<HashSet<string>> PickersAsync(IDatabase redis, string matchId) =>
        matchId.Length == 0 ? [] : [.. (await redis.SetMembersAsync(PickersKey(matchId))).Select(v => v.ToString())];

    /// <summary>
    /// Before the game that follows <paramref name="previousMatchId"/> with the same players and no lobby in between (a
    /// ranked set's next game, a Casual rematch): the session of every player who went into that match as Random (and of
    /// <paramref name="alsoPickers"/>, known to be from an earlier game) holds the Random pick again, so the next match
    /// config rolls a new fighter. Only a session that is still there: a player who is gone gets none made of two fields.
    /// Returns the players it was done for.
    /// </summary>
    public static async Task<IReadOnlyList<string>> RollAgainAsync(IDatabase redis, string previousMatchId, IEnumerable<string>? alsoPickers = null)
    {
        var pickers = await PickersAsync(redis, previousMatchId);
        pickers.UnionWith(alsoPickers ?? []);
        var again = new List<string>();
        foreach (string id in pickers.Order(StringComparer.Ordinal))
        {
            if (await redis.KeyExistsAsync($"connections:{id}"))
            {
                await PlayedLoadout.RecordAsync(redis, id, Character, Skin);
                again.Add(id);
            }
        }

        return again;
    }

    /// <summary>
    /// The fighter a player played in <paramref name="matchId"/>: their lobby pick (player:{id}), or, when that is the
    /// Random fighter, the real one rolled for that match (random_pick:{match}:{id}), else the one their session holds
    /// (connections:{id}: their latest match's).
    /// </summary>
    public static async Task<(string Character, string Skin)> PlayedAsync(IDatabase redis, string playerId, string? matchId = null)
    {
        var picked = await redis.HashGetAsync($"player:{playerId}", ["character", "skin"]);
        if (!IsRandom(picked[0]))
        {
            return (picked[0].ToString(), picked[1].ToString());
        }

        if (matchId is { Length: > 0 } && ((string?)await redis.StringGetAsync($"random_pick:{matchId}:{playerId}"))?.Split('|') is [{ Length: > 0 } rolled, { Length: > 0 } rolledSkin])
        {
            return (rolled, rolledSkin);
        }

        var played = await redis.HashGetAsync($"connections:{playerId}", ["character", "skin"]);
        return (played[0].ToString(), played[1].ToString());
    }
}

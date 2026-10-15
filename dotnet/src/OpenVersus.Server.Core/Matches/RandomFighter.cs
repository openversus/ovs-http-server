using StackExchange.Redis;

namespace OpenVersus.Server.Core.Matches;

// The Random fighter (Tuggernuts, 2026-10-14): character select's "Random" entry (the OVS pak's character_ovs_random, the
// game's "?" art; its skin skin_ovs_random) is a pick, not a fighter. Every match config is built by
// GameplayConfigs.BuildAsync, which calls ResolveAsync first for each player: one whose match loadout (connections:{id},
// copied from the lobby pick player:{id} by PlayedLoadout when the match starts; a bot's bot_config:{id}) is the Random
// fighter gets a random real one there, in a random skin of theirs (BotFighters: the released characters and their
// skins). So the config, match_characters, ratings, stats and the live matches page all see the real fighter.
// - The lobby pick (player:{id}, Mongo) is never changed: everyone keeps seeing "?" in the lobby, and the next match
//   rolls again.
// - A ranked set keeps its fighter: its next games do not copy the lobby pick into the session again (RankedSets), so
//   they find the real one already there. A Casual rematch does not either; a custom lobby's next match does (a new roll).
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
        }
        else
        {
            await redis.HashSetAsync(loadoutKey, [new HashEntry("character", character), new HashEntry("skin", skin)]);
        }

        return (character, skin);
    }

    /// <summary>
    /// The fighter a player played: their lobby pick (player:{id}), or, when that is the Random fighter, the real one
    /// their session holds (connections:{id}, until their next match).
    /// </summary>
    public static async Task<(string Character, string Skin)> PlayedAsync(IDatabase redis, string playerId)
    {
        var picked = await redis.HashGetAsync($"player:{playerId}", ["character", "skin"]);
        if (!IsRandom(picked[0]))
        {
            return (picked[0].ToString(), picked[1].ToString());
        }

        var played = await redis.HashGetAsync($"connections:{playerId}", ["character", "skin"]);
        return (played[0].ToString(), played[1].ToString());
    }
}

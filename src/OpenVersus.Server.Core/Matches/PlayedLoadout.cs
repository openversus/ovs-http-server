using StackExchange.Redis;

namespace OpenVersus.Server.Core.Matches;

/// <summary>
/// The loadout a player goes into a match with, copied into their session (connections:{id}, and the IP-keyed copy while
/// it is theirs), as the TS server's matchmaking request copies player:{id} there (matches.ts). The session's character is
/// what the solo lobby a player gets on leaving a lobby shows (leave_player_lobby: the TS server's shared.routes.ts reads
/// it the same way, Shaggy when there is none), and what perks and ranked data read. Every path that starts a match for a
/// player records it; one that does not leaves the session's previous character behind.
/// </summary>
public static class PlayedLoadout
{
    public static async Task RecordAsync(IDatabase redis, string playerId, string character, string skin, string? profileIcon = null)
    {
        HashEntry[] fields = profileIcon is { Length: > 0 } icon
            ? [new("character", character), new("skin", skin), new("profileIcon", icon)]
            : [new("character", character), new("skin", skin)];
        await redis.HashSetAsync($"connections:{playerId}", fields);
        // The TS server's redisUpdateIpMirror: only while the IP's copy is this player's.
        if ((string?)await redis.HashGetAsync($"connections:{playerId}", "current_ip") is { Length: > 0 } ip
            && (string?)await redis.HashGetAsync($"connections:{ip}", "id") == playerId)
        {
            await redis.HashSetAsync($"connections:{ip}", fields);
        }
    }
}

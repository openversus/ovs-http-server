using StackExchange.Redis;

namespace OpenVersus.Server.Core.Lobbies;

// Where lobbies live (Lobby): every read and write of a lobby's record, its players' pointers to it and its party's ready
// set goes through here, so the lobby services and the code outside them (matchmaking, the match end, the rollback
// callbacks, rifts) share one idea of what a lobby is.
//
// Redis, the TS server's keys (MIGRATION-BRIDGES.md 2):
//   lobby:{id}            the record (Lobby); EX 8 h with 2+ players, else 1 h, unless the caller says otherwise
//   player_lobby:{player} the lobby the player is in
//   party_ready:{lobby}   the party's ready players (a set)
//
// A change (UpdateAsync) is a compare-and-set: the record is read, changed in C#, and written only if it is still what
// was read (a transaction conditioned on the value); otherwise the change runs again on the new value. Two players
// changing one lobby at once both land. The change can therefore run more than once: it must only change the lobby it is
// given. After Attempts rounds that each lost to another writer, LobbyConflictException is thrown; no caller catches it
// yet, so the route answers 500 (the TS server's last writer won instead, losing the other change).

/// <summary>What <see cref="LobbyStore.UpdateAsync"/> does with the lobby a change returns.</summary>
public readonly record struct LobbyWrite(LobbyWrite.Action What, TimeSpan? Ttl = null)
{
    public enum Action { Keep, Save, Delete }

    /// <summary>Nothing is written.</summary>
    public static LobbyWrite Keep => new(Action.Keep);

    /// <summary>Written, for <see cref="LobbyStore.TtlOf"/>.</summary>
    public static LobbyWrite Save => new(Action.Save);

    /// <summary>Written, for <paramref name="ttl"/>.</summary>
    public static LobbyWrite SaveFor(TimeSpan ttl) => new(Action.Save, ttl);

    /// <summary>The record is deleted.</summary>
    public static LobbyWrite Delete => new(Action.Delete);
}

/// <summary>A lobby change lost to other writers <see cref="LobbyStore.Attempts"/> times in a row.</summary>
public sealed class LobbyConflictException(string lobbyId)
    : Exception($"lobby:{lobbyId} changed under {LobbyStore.Attempts} attempts in a row to change it");

public static class LobbyStore
{
    public static readonly TimeSpan PartyTtl = TimeSpan.FromHours(8);
    public static readonly TimeSpan SoloTtl = TimeSpan.FromHours(1);

    /// <summary>Rounds a change gets before <see cref="LobbyConflictException"/>.</summary>
    public const int Attempts = 10;

    public static string Key(string lobbyId) => $"lobby:{lobbyId}";

    public static string PointerKey(string playerId) => $"player_lobby:{playerId}";

    public static string ReadyKey(string lobbyId) => $"party_ready:{lobbyId}";

    /// <summary>redisSaveLobbyState: 8 h with 2+ players, else 1 h.</summary>
    public static TimeSpan TtlOf(Lobby lobby) => lobby.PlayerIds.Count >= 2 ? PartyTtl : SoloTtl;

    /// <summary>The lobby; null when there is none, or its record is not one (<see cref="Lobby.FromJson"/>).</summary>
    public static async Task<Lobby?> GetAsync(IDatabase redis, string lobbyId) =>
        await redis.StringGetAsync(Key(lobbyId)) is { HasValue: true } raw ? Lobby.FromJson(lobbyId, raw.ToString()) : null;

    /// <summary>Writes the lobby whatever is stored (a new lobby), for <paramref name="ttl"/> or <see cref="TtlOf"/>.</summary>
    public static Task SaveAsync(IDatabase redis, Lobby lobby, TimeSpan? ttl = null) =>
        redis.StringSetAsync(Key(lobby.Id), lobby.ToJson(), ttl ?? TtlOf(lobby));

    public static Task DeleteAsync(IDatabase redis, string lobbyId) => redis.KeyDeleteAsync(Key(lobbyId));

    /// <summary>
    /// Changes the lobby (see the header): <paramref name="change"/> gets it as stored and says what to write. The lobby
    /// as changed, or as read when nothing was written; null when there is no lobby.
    /// </summary>
    public static async Task<Lobby?> UpdateAsync(IDatabase redis, string lobbyId, Func<Lobby, LobbyWrite> change)
    {
        string key = Key(lobbyId);
        for (int attempt = 0; attempt < Attempts; attempt++)
        {
            var raw = await redis.StringGetAsync(key);
            if (!raw.HasValue || Lobby.FromJson(lobbyId, raw.ToString()) is not { } lobby)
            {
                return null;
            }

            var write = change(lobby);
            if (write.What == LobbyWrite.Action.Keep)
            {
                return lobby;
            }

            var transaction = redis.CreateTransaction();
            transaction.AddCondition(Condition.StringEqual(key, raw));
            _ = write.What == LobbyWrite.Action.Delete
                ? transaction.KeyDeleteAsync(key)
                : transaction.StringSetAsync(key, lobby.ToJson(), write.Ttl ?? TtlOf(lobby));
            if (await transaction.ExecuteAsync())
            {
                return lobby;
            }
        }

        throw new LobbyConflictException(lobbyId);
    }

    /// <summary>The lobby the player is in (player_lobby); null for none.</summary>
    public static async Task<string?> PointerAsync(IDatabase redis, string playerId) =>
        (string?)await redis.StringGetAsync(PointerKey(playerId)) is { Length: > 0 } lobbyId ? lobbyId : null;

    public static Task SetPointerAsync(IDatabase redis, string playerId, string lobbyId, TimeSpan ttl) =>
        redis.StringSetAsync(PointerKey(playerId), lobbyId, ttl);

    public static Task ClearPointerAsync(IDatabase redis, string playerId) => redis.KeyDeleteAsync(PointerKey(playerId));

    /// <summary>The lobby's players, in the order they joined; null when there is no lobby.</summary>
    public static async Task<List<string>?> PlayersOfAsync(IDatabase redis, string lobbyId) => (await GetAsync(redis, lobbyId))?.PlayerIds;

    /// <summary>The party's ready players are forgotten (a match starts or is called off).</summary>
    public static Task ResetReadyAsync(IDatabase redis, string lobbyId) => redis.KeyDeleteAsync(ReadyKey(lobbyId));

    /// <summary><see cref="ResetReadyAsync"/> for the lobby the player is in, if any.</summary>
    public static async Task ResetReadyOfAsync(IDatabase redis, string playerId)
    {
        if (await PointerAsync(redis, playerId) is { } lobbyId)
        {
            await ResetReadyAsync(redis, lobbyId);
        }
    }
}

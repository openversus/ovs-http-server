using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Realtime;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Lobbies;

/// <summary>
/// An invite into a lobby, as the game's invite popup reads it (InviteReceivedForLobby): ContextData.LobbyType is its
/// origin (the game's ELobbyInviteOrigin: Party, CustomGame, Rift, Arena), which decides what the invited game sends on
/// accepting (an Arena invite: join_arena_lobby; bench, 2026-10-10). LobbyType is the Hydra lobby type (EHydraLobbyType
/// BlindPick 0). The invited player's next join goes to the lobby for a minute (pending_join_lobby).
/// </summary>
public static class LobbyInvites
{
    public const string Party = "Party", Arena = "Arena";

    /// <summary>The origin an invite to <paramref name="lobby"/> carries.</summary>
    public static string OriginOf(Lobby? lobby) => lobby is ArenaLobby ? Arena : Party;

    public static async Task SendAsync(IDatabase redis, string invitee, string lobbyId, string origin, string inviterId)
    {
        await ProfileNotifications.SendAsync(redis, invitee, new JsonObject
        {
            ["template_id"] = "InviteReceivedForLobby",
            ["LobbyType"] = 0,
            ["MatchID"] = lobbyId,
            ["ContextData"] = new JsonObject { ["LobbyType"] = origin },
            ["IsSpectator"] = false,
            ["InviterAccountId"] = inviterId,
        });
        await redis.StringSetAsync($"pending_join_lobby:{invitee}", lobbyId, TimeSpan.FromSeconds(60));
    }
}

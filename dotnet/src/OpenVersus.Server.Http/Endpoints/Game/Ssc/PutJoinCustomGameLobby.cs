using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/join_custom_game_lobby.
/// Seen in: binary ssc name; TS server: PUT /ssc/invoke/join_custom_game_lobby.
/// Ssc: server/capture; not a whole string in the exe (likely built inline); the server implements it because the game calls it.
/// </summary>
public sealed class PutJoinCustomGameLobby : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/join_custom_game_lobby");
    }
}

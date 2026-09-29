using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/kick_from_lobby.
/// Seen in: binary ssc name; TS server: PUT /ssc/invoke/kick_from_lobby.
/// Ssc: binary.
/// </summary>
public sealed class PutKickFromLobby : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/kick_from_lobby");
    }
}

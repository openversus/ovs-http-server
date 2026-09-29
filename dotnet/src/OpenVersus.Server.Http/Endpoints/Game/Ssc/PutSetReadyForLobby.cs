using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/set_ready_for_lobby.
/// Seen in: binary ssc name; captured 10x; TS server: PUT /ssc/invoke/set_ready_for_lobby.
/// Ssc: server/capture.
/// </summary>
public sealed class PutSetReadyForLobby : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/set_ready_for_lobby");
    }
}

using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/set_lobby_joinable.
/// Seen in: binary ssc name; captured 5x; TS server: PUT /ssc/invoke/set_lobby_joinable.
/// Ssc: server/capture.
/// </summary>
public sealed class PutSetLobbyJoinable : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/set_lobby_joinable");
    }
}

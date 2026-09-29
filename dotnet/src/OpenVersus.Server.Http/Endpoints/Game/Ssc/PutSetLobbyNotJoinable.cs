using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/set_lobby_not_joinable.
/// Seen in: binary ssc name; captured 1x; TS server: PUT /ssc/invoke/set_lobby_not_joinable.
/// Ssc: server/capture.
/// </summary>
public sealed class PutSetLobbyNotJoinable : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/set_lobby_not_joinable");
    }
}

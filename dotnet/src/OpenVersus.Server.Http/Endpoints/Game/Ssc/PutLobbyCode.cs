using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/lobby_code.
/// Seen in: binary ssc name; TS server: PUT /ssc/invoke/lobby_code.
/// Ssc: server/capture.
/// </summary>
public sealed class PutLobbyCode : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/lobby_code");
    }
}

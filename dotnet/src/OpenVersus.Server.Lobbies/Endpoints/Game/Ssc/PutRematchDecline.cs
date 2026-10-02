using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Lobbies.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/rematch_decline.
/// Seen in: binary ssc name; TS server: PUT /ssc/invoke/rematch_decline.
/// Ssc: server/capture.
/// </summary>
public sealed class PutRematchDecline : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/rematch_decline");
    }
}

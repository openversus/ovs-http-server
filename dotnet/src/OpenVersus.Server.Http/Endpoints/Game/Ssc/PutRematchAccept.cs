using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/rematch_accept.
/// Seen in: binary ssc name; TS server: PUT /ssc/invoke/rematch_accept.
/// Ssc: server/capture.
/// </summary>
public sealed class PutRematchAccept : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/rematch_accept");
    }
}

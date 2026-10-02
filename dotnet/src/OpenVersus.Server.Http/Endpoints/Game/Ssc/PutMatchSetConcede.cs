using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/match_set_concede.
/// Seen in: binary ssc name; TS server: PUT /ssc/invoke/match_set_concede.
/// Ssc: server/capture.
/// </summary>
public sealed class PutMatchSetConcede : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/match_set_concede");
    }
}

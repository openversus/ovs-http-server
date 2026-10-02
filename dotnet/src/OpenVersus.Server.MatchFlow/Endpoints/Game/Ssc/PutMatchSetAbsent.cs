using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.MatchFlow.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/match_set_absent.
/// Seen in: binary ssc name; captured 2x; TS server: PUT /ssc/invoke/match_set_absent.
/// Ssc: server/capture.
/// </summary>
public sealed class PutMatchSetAbsent : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/match_set_absent");
    }
}

using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.MatchFlow.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/match_set_checkin.
/// Seen in: binary ssc name; captured 6x; TS server: PUT /ssc/invoke/match_set_checkin.
/// Ssc: server/capture.
/// </summary>
public sealed class PutMatchSetCheckin : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/match_set_checkin");
    }
}

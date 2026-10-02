using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/submit_end_of_match_stats.
/// Seen in: binary ssc name; captured 9x; TS server: PUT /ssc/invoke/submit_end_of_match_stats.
/// Ssc: server/capture.
/// </summary>
public sealed class PutSubmitEndOfMatchStats : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/submit_end_of_match_stats");
    }
}

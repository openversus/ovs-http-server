using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// GET /ssc/invoke/ranked_data.
/// Seen in: binary ssc name; TS server: GET /ssc/invoke/ranked_data.
/// Ssc: server/capture.
/// </summary>
public sealed class GetRankedData : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/ssc/invoke/ranked_data");
    }
}

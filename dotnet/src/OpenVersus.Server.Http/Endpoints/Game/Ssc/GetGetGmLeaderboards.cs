using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// GET /ssc/invoke/get_gm_leaderboards.
/// Seen in: binary ssc name; TS server: GET /ssc/invoke/get_gm_leaderboards.
/// Ssc: binary.
/// </summary>
public sealed class GetGetGmLeaderboards : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/ssc/invoke/get_gm_leaderboards");
    }
}

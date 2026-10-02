using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Web.Endpoints.OpenVersus.Web;

/// <summary>
/// GET /api/leaderboard/{mode}/me.
/// Seen in: TS server: GET /api/leaderboard/{mode}/me.
/// Server only.
/// </summary>
public sealed class GetApiLeaderboardByModeMe : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/api/leaderboard/{mode}/me");
    }
}

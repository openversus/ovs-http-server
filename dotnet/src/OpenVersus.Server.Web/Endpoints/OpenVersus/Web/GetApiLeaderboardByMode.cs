using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Web.Endpoints.OpenVersus.Web;

/// <summary>
/// GET /api/leaderboard/{mode}.
/// Seen in: TS server: GET /api/leaderboard/{mode}.
/// Server only.
/// </summary>
public sealed class GetApiLeaderboardByMode : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/api/leaderboard/{mode}");
    }
}

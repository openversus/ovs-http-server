using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.OpenVersus.Web;

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

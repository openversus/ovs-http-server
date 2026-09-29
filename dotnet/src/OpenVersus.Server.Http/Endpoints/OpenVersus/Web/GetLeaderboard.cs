using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.OpenVersus.Web;

/// <summary>
/// GET /leaderboard.
/// Seen in: TS server: GET /leaderboard.
/// Server only.
/// </summary>
public sealed class GetLeaderboard : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/leaderboard");
    }
}

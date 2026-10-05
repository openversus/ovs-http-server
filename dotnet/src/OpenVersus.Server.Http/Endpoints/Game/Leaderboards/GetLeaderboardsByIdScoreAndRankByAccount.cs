using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Leaderboards;

/// <summary>
/// GET /leaderboards/{id}/score-and-rank/{account}.
/// Seen in: binary 0x1450654c0.
/// </summary>
public sealed class GetLeaderboardsByIdScoreAndRankByAccount : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/leaderboards/{id}/score-and-rank/{account}");
    }
}

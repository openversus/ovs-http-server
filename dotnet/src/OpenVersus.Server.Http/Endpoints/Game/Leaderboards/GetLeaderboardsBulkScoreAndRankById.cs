using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Leaderboards;

/// <summary>
/// GET /leaderboards/bulk/score-and-rank/{id}.
/// Seen in: binary 0x145065780; captured 84x; TS server: PUT /leaderboards/bulk/score-and-rank/{playerId}.
/// Sent as PUT + x-hydra-http-method: GET.
/// </summary>
public sealed class GetLeaderboardsBulkScoreAndRankById : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/leaderboards/bulk/score-and-rank/{id}");
    }
}

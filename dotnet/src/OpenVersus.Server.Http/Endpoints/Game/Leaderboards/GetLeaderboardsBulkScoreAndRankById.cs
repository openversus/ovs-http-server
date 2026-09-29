using FastEndpoints;
using OpenVersus.Server.Core.Leaderboards;

namespace OpenVersus.Server.Http.Endpoints.Game.Leaderboards;

/// <summary>
/// GET /leaderboards/bulk/score-and-rank/{id}: the player's ranked score and place in 1v1 and 2v2 (<see cref="IRankService"/>).
/// Sent as PUT + x-hydra-http-method: GET (the TS server routes it as PUT). The request body is not read.
/// Seen in: binary 0x145065780; captured 84x; TS server: PUT /leaderboards/bulk/score-and-rank/{playerId}.
/// </summary>
public sealed class GetLeaderboardsBulkScoreAndRankById : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/leaderboards/bulk/score-and-rank/{id}");
    }

    public override async Task HandleAsync(CancellationToken ct) =>
        await SendJsonAsync(await Resolve<IRankService>().ScoreAndRankAsync(Route<string>("id")!, ct), ct);
}

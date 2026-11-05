using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Seasons;

/// <summary>
/// GET /seasons/types/{type}/participant_leaderboards/{id}/score-and-rank/{account}.
/// Seen in: binary 0x1450679d0.
/// Segment order inferred.
/// </summary>
public sealed class GetSeasonsTypesByTypeParticipantLeaderboardsByIdScoreAndRankByAccount : TsCatchAllEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/seasons/types/{type}/participant_leaderboards/{id}/score-and-rank/{account}");
    }
}

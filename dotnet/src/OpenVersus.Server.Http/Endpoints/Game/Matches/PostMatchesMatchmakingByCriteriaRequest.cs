using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Matches;

/// <summary>
/// POST /matches/matchmaking/{criteria}/request.
/// Seen in: binary 0x144fdd630; captured 10x; TS server: POST /matches/matchmaking/1v1-retail/request, POST /matches/matchmaking/2v2-retail/request, POST /matches/matchmaking/ranked-1v1-retail/request.
/// Criteria e.g. 1v1-retail.
/// </summary>
public sealed class PostMatchesMatchmakingByCriteriaRequest : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.POST);
        Routes("/matches/matchmaking/{criteria}/request");
    }
}

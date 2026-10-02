using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Matches;

/// <summary>
/// POST /matches/matchmaking/request/{id}/cancel.
/// Seen in: binary 0x144fd78f0; captured 5x; TS server: POST /matches/matchmaking/request/{id}/cancel.
/// Method from capture.
/// </summary>
public sealed class PostMatchesMatchmakingRequestByIdCancel : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.POST);
        Routes("/matches/matchmaking/request/{id}/cancel");
    }
}

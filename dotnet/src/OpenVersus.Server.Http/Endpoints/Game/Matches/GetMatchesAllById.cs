using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Matches;

/// <summary>
/// GET /matches/all/{id}.
/// Seen in: binary 0x144fdab70; captured 8x; TS server: GET /matches/all/{id}.
/// </summary>
public sealed class GetMatchesAllById : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/matches/all/{id}");
    }
}

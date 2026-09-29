using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Matches;

/// <summary>
/// GET /matches/{id}.
/// Seen in: binary 0x144fda970; TS server: GET /matches/{id}.
/// </summary>
public sealed class GetMatchesById : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/matches/{id}");
    }
}

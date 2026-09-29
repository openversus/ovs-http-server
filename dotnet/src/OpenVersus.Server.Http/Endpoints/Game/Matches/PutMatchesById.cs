using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Matches;

/// <summary>
/// PUT /matches/{id}.
/// Seen in: binary 0x144fde060; captured 11x; TS server: PUT /matches/{id}.
/// </summary>
public sealed class PutMatchesById : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/matches/{id}");
    }
}

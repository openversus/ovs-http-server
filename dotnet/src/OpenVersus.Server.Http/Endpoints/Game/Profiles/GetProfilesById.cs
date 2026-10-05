using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Profiles;

/// <summary>
/// GET /profiles/{id}.
/// Seen in: binary 0x145065a80; TS server: GET /profiles/{id}.
/// Also 0x145065c50.
/// </summary>
public sealed class GetProfilesById : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/profiles/{id}");
    }
}

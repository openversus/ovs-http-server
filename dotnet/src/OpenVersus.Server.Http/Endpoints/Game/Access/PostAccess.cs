using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Access;

/// <summary>
/// POST /access.
/// Seen in: binary 0x144f9f5b0; captured 11x; TS server: POST /access.
/// Method from capture (no enum call).
/// </summary>
public sealed class PostAccess : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.POST);
        Routes("/access");
    }
}

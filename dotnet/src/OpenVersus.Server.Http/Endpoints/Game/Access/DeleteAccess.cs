using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Access;

/// <summary>
/// DELETE /access.
/// Seen in: binary 0x144fab980; captured 9x; TS server: DELETE /access.
/// Method from capture (no enum call).
/// </summary>
public sealed class DeleteAccess : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.DELETE);
        Routes("/access");
    }
}

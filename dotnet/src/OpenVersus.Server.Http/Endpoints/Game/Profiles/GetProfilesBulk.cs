using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Profiles;

/// <summary>
/// GET /profiles/bulk.
/// Seen in: binary 0x145065060; captured 48x; TS server: PUT /profiles/bulk.
/// Sent as PUT + x-hydra-http-method: GET.
/// </summary>
public sealed class GetProfilesBulk : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/profiles/bulk");
    }
}

using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Profiles;

/// <summary>
/// GET /profiles/{id}/inventory.
/// Seen in: binary 0x14505dbe0; captured 11x; TS server: PUT /profiles/{id}/inventory.
/// Sent as PUT + x-hydra-http-method: GET.
/// </summary>
public sealed class GetProfilesByIdInventory : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/profiles/{id}/inventory");
    }
}

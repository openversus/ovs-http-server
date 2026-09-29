using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Profiles;

/// <summary>
/// PUT /profiles/{id}/inventory.
/// Seen in: binary 0x14505dd70; captured 11x; TS server: PUT /profiles/{id}/inventory.
/// Body: modifications. Same wire request as GET /profiles/{id}/inventory (a PUT with x-hydra-http-method: GET); only that header tells them apart.
/// </summary>
public sealed class PutProfilesByIdInventory : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/profiles/{id}/inventory");
    }
}

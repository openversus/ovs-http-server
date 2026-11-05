using FastEndpoints;

namespace OpenVersus.Server.Http.Endpoints.Game.Profiles;

/// <summary>
/// PUT /profiles/{id}/inventory, body: modifications (<see cref="InventoryEndpoint"/>: the TS handler reads none and
/// answers the inventory, as for the GET). Same wire request as GET /profiles/{id}/inventory (a PUT with
/// x-hydra-http-method: GET); only that header tells them apart.
/// Seen in: binary 0x14505dd70; captured 11x; TS server: PUT /profiles/{id}/inventory.
/// </summary>
public sealed class PutProfilesByIdInventory : InventoryEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/profiles/{id}/inventory");
    }
}

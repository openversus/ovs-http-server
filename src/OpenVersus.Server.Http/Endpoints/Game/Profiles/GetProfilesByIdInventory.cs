using FastEndpoints;

namespace OpenVersus.Server.Http.Endpoints.Game.Profiles;

/// <summary>
/// GET /profiles/{id}/inventory (<see cref="InventoryEndpoint"/>). Sent as PUT + x-hydra-http-method: GET; a plain PUT
/// is <see cref="PutProfilesByIdInventory"/> (the TS server routes both as PUT).
/// Seen in: binary 0x14505dbe0; captured 11x; TS server: PUT /profiles/{id}/inventory.
/// </summary>
public sealed class GetProfilesByIdInventory : InventoryEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/profiles/{id}/inventory");
    }
}

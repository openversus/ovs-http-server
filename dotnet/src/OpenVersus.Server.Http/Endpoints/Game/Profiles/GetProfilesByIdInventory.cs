using FastEndpoints;
using OpenVersus.Server.Core.Identity;
using OpenVersus.Server.Core.Inventory;
using OpenVersus.Server.Http.Shared.Endpoints;
using OpenVersus.Server.Http.Shared.Hosting;

namespace OpenVersus.Server.Http.Endpoints.Game.Profiles;

/// <summary>
/// GET /profiles/{id}/inventory: the requesting player's inventory (<see cref="IInventoryService"/>). The route's id is
/// not read, as there: the player is the one the request resolves to (<see cref="IAccountResolver"/>), else the token's.
/// Sent as PUT + x-hydra-http-method: GET; a plain PUT is another route (the TS server routes both as PUT).
/// Seen in: binary 0x14505dbe0; captured 11x; TS server: PUT /profiles/{id}/inventory.
/// </summary>
public sealed class GetProfilesByIdInventory : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/profiles/{id}/inventory");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var resolved = await Resolve<IAccountResolver>().ResolveAsync(AccountLookups.From(HttpContext));
        string accountId = resolved?.Id ?? HttpContext.Session()?.AccountId ?? "";
        if (await Resolve<IInventoryService>().InventoryAsync(accountId, ct) is { } inventory)
        {
            await SendJsonAsync(inventory, ct);
        }
        else
        {
            await Send.ResultAsync(Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
        }
    }
}

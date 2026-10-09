using FastEndpoints;
using OpenVersus.Server.Core.Identity;
using OpenVersus.Server.Core.Inventory;
using OpenVersus.Server.Http.Shared.Endpoints;
using OpenVersus.Server.Http.Shared.Hosting;

namespace OpenVersus.Server.Http.Endpoints.Game.Profiles;

/// <summary>
/// /profiles/{id}/inventory: the requesting player's inventory (<see cref="IInventoryService"/>). The route's id is not
/// read, as in the TS server: the player is the one the request resolves to (<see cref="IAccountResolver"/>), else the
/// token's. The TS server routes the GET (sent as PUT + x-hydra-http-method: GET) and the plain PUT (modifications in
/// the body) to one handler that reads no body: both answer the inventory, nothing is applied.
/// </summary>
public abstract class InventoryEndpoint : JsonBodyEndpoint
{
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

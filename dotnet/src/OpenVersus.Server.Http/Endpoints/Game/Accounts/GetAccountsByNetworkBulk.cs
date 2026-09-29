using FastEndpoints;
using OpenVersus.Server.Core.Profiles;

namespace OpenVersus.Server.Http.Endpoints.Game.Accounts;

/// <summary>
/// GET /accounts/{network}/bulk: for wb_network, the account details of the ids asked (ProfilesService); the TS server
/// has no route for another network. Sent as PUT + x-hydra-http-method: GET (the TS server routes it as PUT).
/// Seen in: binary 0x144fd76d0; captured 9x; TS server: PUT /accounts/wb_network/bulk.
/// </summary>
public sealed class GetAccountsByNetworkBulk : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/accounts/{network}/bulk");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (Route<string>("network") != "wb_network")
        {
            await SendNotPortedAsync();
            return;
        }

        await SendJsonAsync(await Resolve<IProfilesService>().WbNetworkAccountsAsync(await ReadBodyAsync(ct), ct), ct);
    }
}

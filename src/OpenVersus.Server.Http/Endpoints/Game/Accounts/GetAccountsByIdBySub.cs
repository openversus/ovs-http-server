using FastEndpoints;
using OpenVersus.Server.Core.Profiles;
using OpenVersus.Server.Http.Shared.Endpoints;

namespace OpenVersus.Server.Http.Endpoints.Game.Accounts;

/// <summary>
/// GET /accounts/{id}/{sub}: for wb_network, one account by id or public id (<see cref="IProfilesService.WbNetworkAccountAsync"/>,
/// the TS GET /accounts/wb_network/:id); the TS server has no route for another first segment (its catch-all answered).
/// /accounts/{network}/bulk is its own endpoint (the literal wins).
/// Seen in: binary 0x144fda790; TS server: GET /accounts/wb_network/{id}.
/// </summary>
public sealed class GetAccountsByIdBySub : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/accounts/{id}/{sub}");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (Route<string>("id") != "wb_network")
        {
            await SendTsCatchAllAsync(ct);
            return;
        }

        await SendJsonAsync(await Resolve<IProfilesService>().WbNetworkAccountAsync(Route<string>("sub") ?? "", ct), ct);
    }
}

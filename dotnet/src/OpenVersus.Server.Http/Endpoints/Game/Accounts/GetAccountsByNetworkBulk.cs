using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Accounts;

/// <summary>
/// GET /accounts/{network}/bulk.
/// Seen in: binary 0x144fd76d0; captured 9x; TS server: PUT /accounts/wb_network/bulk.
/// Sent as PUT + x-hydra-http-method: GET (capture: wb_network).
/// </summary>
public sealed class GetAccountsByNetworkBulk : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/accounts/{network}/bulk");
    }
}

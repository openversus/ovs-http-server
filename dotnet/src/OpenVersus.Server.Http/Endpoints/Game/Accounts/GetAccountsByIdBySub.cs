using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Accounts;

/// <summary>
/// GET /accounts/{id}/{sub}.
/// Seen in: binary 0x144fda790; TS server: GET /accounts/wb_network/{id}.
/// Second segment's name not read yet.
/// </summary>
public sealed class GetAccountsByIdBySub : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/accounts/{id}/{sub}");
    }
}

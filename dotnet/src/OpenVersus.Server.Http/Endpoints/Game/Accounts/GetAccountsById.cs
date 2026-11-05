using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Accounts;

/// <summary>
/// GET /accounts/{id}.
/// Seen in: binary 0x144fda420; TS server: GET /accounts/{id}.
/// Also 0x144fda5f0.
/// </summary>
public sealed class GetAccountsById : TsCatchAllEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/accounts/{id}");
    }
}

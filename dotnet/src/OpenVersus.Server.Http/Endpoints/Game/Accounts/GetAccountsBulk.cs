using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Accounts;

/// <summary>
/// GET /accounts/bulk.
/// Seen in: binary 0x144fd7550.
/// Method inferred: sibling of the {network} variant; sent as PUT + override.
/// </summary>
public sealed class GetAccountsBulk : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/accounts/bulk");
    }
}

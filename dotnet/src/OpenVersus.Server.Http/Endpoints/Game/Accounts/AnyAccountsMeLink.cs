using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Accounts;

/// <summary>
/// Any method /accounts/me/link.
/// Seen in: binary fragment.
/// Binary fragment; no builder found yet.
/// </summary>
public sealed class AnyAccountsMeLink : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET, FastEndpoints.Http.PUT, FastEndpoints.Http.POST, FastEndpoints.Http.DELETE);
        Routes("/accounts/me/link");
    }
}

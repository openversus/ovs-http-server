using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Accounts;

/// <summary>
/// Any method /accounts/me/notifications/{id}.
/// Seen in: binary 0x145060de0; TS server: GET /accounts/me/notifications/bulk.
/// Method not read yet.
/// </summary>
public sealed class AnyAccountsMeNotificationsById : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET, FastEndpoints.Http.PUT, FastEndpoints.Http.POST, FastEndpoints.Http.DELETE);
        Routes("/accounts/me/notifications/{id}");
    }
}

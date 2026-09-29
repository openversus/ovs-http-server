using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Accounts;

/// <summary>
/// Any method /accounts/me/notifications.
/// Seen in: binary 0x145060180; TS server: GET /accounts/me/notifications.
/// Method not read yet.
/// </summary>
public sealed class AnyAccountsMeNotifications : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET, FastEndpoints.Http.PUT, FastEndpoints.Http.POST, FastEndpoints.Http.DELETE);
        Routes("/accounts/me/notifications");
    }
}

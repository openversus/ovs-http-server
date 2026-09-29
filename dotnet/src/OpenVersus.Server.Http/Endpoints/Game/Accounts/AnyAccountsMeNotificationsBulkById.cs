using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Accounts;

/// <summary>
/// Any method /accounts/me/notifications/bulk/{id}.
/// Seen in: binary fragment; TS server: DELETE /accounts/me/notifications/bulk/{notificationId}.
/// Binary fragment; no builder found yet.
/// </summary>
public sealed class AnyAccountsMeNotificationsBulkById : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET, FastEndpoints.Http.PUT, FastEndpoints.Http.POST, FastEndpoints.Http.DELETE);
        Routes("/accounts/me/notifications/bulk/{id}");
    }
}

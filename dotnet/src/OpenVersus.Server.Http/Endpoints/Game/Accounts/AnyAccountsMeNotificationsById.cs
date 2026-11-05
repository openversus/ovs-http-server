using FastEndpoints;
using OpenVersus.Server.Http.Shared.Endpoints;

namespace OpenVersus.Server.Http.Endpoints.Game.Accounts;

/// <summary>
/// /accounts/me/notifications/{id}: GET .../bulk answers no notifications (<see cref="AnyAccountsMeNotifications.None"/>),
/// as the TS server does; any other id or method has no TS route (its catch-all).
/// Seen in: binary 0x145060de0; TS server: GET /accounts/me/notifications/bulk.
/// </summary>
public sealed class AnyAccountsMeNotificationsById : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET, FastEndpoints.Http.PUT, FastEndpoints.Http.POST, FastEndpoints.Http.DELETE);
        Routes("/accounts/me/notifications/{id}");
    }

    public override Task HandleAsync(CancellationToken ct) =>
        HttpContext.Request.Method == "GET" && Route<string>("id") == "bulk" ? SendJsonAsync(AnyAccountsMeNotifications.None(), ct) : SendTsCatchAllAsync(ct);
}

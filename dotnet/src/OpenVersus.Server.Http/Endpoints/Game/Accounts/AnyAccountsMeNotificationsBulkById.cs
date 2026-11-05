using FastEndpoints;
using OpenVersus.Server.Http.Shared.Endpoints;

namespace OpenVersus.Server.Http.Endpoints.Game.Accounts;

/// <summary>
/// /accounts/me/notifications/bulk/{id}: DELETE is the game acknowledging a persistent Hydra websocket notification it
/// has handled; 204, nothing to keep, as the TS server answers. Any other method has no TS route (its catch-all).
/// Seen in: binary fragment; TS server: DELETE /accounts/me/notifications/bulk/{notificationId}.
/// </summary>
public sealed class AnyAccountsMeNotificationsBulkById : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET, FastEndpoints.Http.PUT, FastEndpoints.Http.POST, FastEndpoints.Http.DELETE);
        Routes("/accounts/me/notifications/bulk/{id}");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (HttpContext.Request.Method != "DELETE")
        {
            await SendTsCatchAllAsync(ct);
            return;
        }

        Logger.LogDebug("Notification {Id} acknowledged by the game", Route<string>("id"));
        await Send.NoContentAsync(ct);
    }
}

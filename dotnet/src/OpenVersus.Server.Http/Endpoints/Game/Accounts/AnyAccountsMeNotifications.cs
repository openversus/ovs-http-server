using System.Text.Json.Nodes;
using FastEndpoints;
using OpenVersus.Server.Http.Shared.Endpoints;

namespace OpenVersus.Server.Http.Endpoints.Game.Accounts;

/// <summary>
/// /accounts/me/notifications: GET answers no notifications, <c>{notifications: [], total: 0}</c>, as the TS server does
/// (invites and the like reach the game over the websocket); the TS server has no other method here (its catch-all).
/// Seen in: binary 0x145060180; TS server: GET /accounts/me/notifications.
/// </summary>
public sealed class AnyAccountsMeNotifications : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET, FastEndpoints.Http.PUT, FastEndpoints.Http.POST, FastEndpoints.Http.DELETE);
        Routes("/accounts/me/notifications");
    }

    public override Task HandleAsync(CancellationToken ct) =>
        HttpContext.Request.Method == "GET" ? SendJsonAsync(None(), ct) : SendTsCatchAllAsync(ct);

    /// <summary>What the TS server answers for the notification lists: none.</summary>
    public static JsonObject None() => new() { ["notifications"] = new JsonArray(), ["total"] = 0 };
}

using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Compat;
using FastEndpoints;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Web.Site;
using StackExchange.Redis;
using OpenVersus.Server.Core.Realtime;

namespace OpenVersus.Server.Web.Endpoints.OpenVersus.Admin;

/// <summary>
/// POST /api/admin/banner {title?, message?, timeout?, targetPlayerId?}: an in-game banner (an admin_banner client
/// notification, title to 255 and message to 500 characters, timeout 8 s unless a number) to one player or to every
/// online player; {ok, delivered, attempted} or {ok, delivered: 0, note}. Behind the admin gate (JSON 401). TS server: POST /api/admin/banner.
/// Seen in: TS server: POST /api/admin/banner.
/// </summary>
public sealed class PostApiAdminBanner : EndpointWithoutRequest
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.POST);
        Routes("/api/admin/banner");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!await AdminGate.PassAsync(HttpContext, api: true))
        {
            return;
        }

        HttpContext.Response.ContentType = "application/json; charset=utf-8";
        try
        {
            JsonObject body;
            try
            {
                body = await JsonNode.ParseAsync(HttpContext.Request.Body, cancellationToken: ct) as JsonObject ?? [];
            }
            catch (System.Text.Json.JsonException)
            {
                body = [];
            }

            string title = Text(body["title"]), message = Text(body["message"]);
            double timeout = body["timeout"] is JsonValue tv && tv.TryGetValue(out double t) && double.IsFinite(t) ? t : 8;
            string? target = body["targetPlayerId"] is JsonValue pv && pv.TryGetValue(out string? p) && p.Length > 0 ? p : null;
            if (title.Length > 255)
            {
                title = title[..255];
            }

            if (message.Length > 500)
            {
                message = message[..500];
            }

            if (title.Length == 0 && message.Length == 0)
            {
                HttpContext.Response.StatusCode = 400;
                await HttpContext.Response.WriteAsync(new JsonObject { ["error"] = "title or message is required" }.ToJsonString(), ct);
                return;
            }

            var redis = Resolve<IServiceProvider>().GetService<IConnectionMultiplexer>()?.GetDatabase() ?? throw new InvalidOperationException("this service has no Redis (REDIS)");
            var targets = target is not null ? [target] : (await redis.SetMembersAsync("online_players")).Select(v => v.ToString()).ToList();
            if (targets.Count == 0)
            {
                await HttpContext.Response.WriteAsync(new JsonObject { ["ok"] = true, ["delivered"] = 0, ["note"] = "No online players" }.ToJsonString(), ct);
                return;
            }

            int delivered = 0;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (string id in targets)
            {
                try
                {
                    await PlayerMessages.NotifyClientAsync(redis, id, "admin_banner", title, message, new JsonObject { ["timeout"] = timeout }, now);
                    delivered++;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    Logger.LogError("Failed to push admin banner to {Player}: {Error}", id, e.Message);
                }
            }

            Logger.LogInformation("Admin banner broadcast: delivered to {Delivered}/{Attempted} players; title=\"{Title}\" message=\"{Message}\"", delivered, targets.Count, title, message);
            await HttpContext.Response.WriteAsync(new JsonObject { ["ok"] = true, ["delivered"] = delivered, ["attempted"] = targets.Count }.ToJsonString(), ct);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Logger.LogError("Error in POST /api/admin/banner: {Error}", e.Message);
            HttpContext.Response.StatusCode = 500;
            await HttpContext.Response.WriteAsync(new JsonObject { ["error"] = e.ToString() }.ToJsonString(), ct);
        }
    }

    // (body.x || "").toString()
    private static string Text(JsonNode? node) => node is JsonValue v ? (v.TryGetValue(out string? s) ? s : Js.Stringify(v)) : node?.ToJsonString() ?? "";
}

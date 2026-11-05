using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Compat;
using FastEndpoints;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Web.Site;
using StackExchange.Redis;

namespace OpenVersus.Server.Web.Endpoints.OpenVersus.Admin;

/// <summary>
/// GET /api/admin/banner/online-count: {count} of online players, behind the admin gate (JSON 401). TS server: GET /api/admin/banner/online-count.
/// Seen in: TS server: GET /api/admin/banner/online-count.
/// </summary>
public sealed class GetApiAdminBannerOnlineCount : EndpointWithoutRequest
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/api/admin/banner/online-count");
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
            long count = Resolve<IServiceProvider>().GetService<IConnectionMultiplexer>()?.GetDatabase() is { } redis ? await redis.SetLengthAsync("online_players") : throw new InvalidOperationException("this service has no Redis (REDIS)");
            await HttpContext.Response.WriteAsync(new JsonObject { ["count"] = count }.ToJsonString(), ct);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            HttpContext.Response.StatusCode = 500;
            await HttpContext.Response.WriteAsync(new JsonObject { ["error"] = e.ToString() }.ToJsonString(), ct);
        }
    }
}

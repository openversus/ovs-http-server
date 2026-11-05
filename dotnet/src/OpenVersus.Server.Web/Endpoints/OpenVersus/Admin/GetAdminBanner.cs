using System.Text.Json.Nodes;
using FastEndpoints;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Web.Site;
using StackExchange.Redis;

namespace OpenVersus.Server.Web.Endpoints.OpenVersus.Admin;

/// <summary>
/// GET /admin/banner: the admin banner page (admin.html, with how many players are online), behind the admin gate
/// (<see cref="AdminGate"/>: ?pw= once, then the cookie). TS server: GET /admin/banner.
/// Seen in: TS server: GET /admin/banner.
/// </summary>
public sealed class GetAdminBanner : EndpointWithoutRequest
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/admin/banner");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!await AdminGate.PassAsync(HttpContext, api: false))
        {
            return;
        }

        try
        {
            long online = Resolve<IServiceProvider>().GetService<IConnectionMultiplexer>()?.GetDatabase() is { } redis ? await redis.SetLengthAsync("online_players") : 0;
            await Pages.SendAsync(HttpContext, Pages.AdminBanner(online));
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Logger.LogError("Error in GET /admin/banner: {Error}", e.Message);
            await Pages.SendTextAsync(HttpContext, "Error loading admin banner page", StatusCodes.Status500InternalServerError);
        }
    }
}

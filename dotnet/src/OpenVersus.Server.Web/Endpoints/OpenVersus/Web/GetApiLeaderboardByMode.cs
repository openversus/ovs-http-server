using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Compat;
using FastEndpoints;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Web.Site;
using StackExchange.Redis;
using OpenVersus.Server.Core.Leaderboards;

namespace OpenVersus.Server.Web.Endpoints.OpenVersus.Web;

/// <summary>
/// GET /api/leaderboard/{mode}?character=: the website's board (<see cref="ILeaderboardService.WebBoardAsync"/>; "global"
/// or no character: every player). 400 for a mode that is not 1v1 or 2v2, 500 when the read fails, as the TS route.
/// Seen in: TS server: GET /api/leaderboard/{mode}.
/// </summary>
public sealed class GetApiLeaderboardByMode : EndpointWithoutRequest
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/api/leaderboard/{mode}");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        string mode = Route<string>("mode") ?? "";
        if (mode is not ("1v1" or "2v2"))
        {
            await Json(new JsonObject { ["error"] = "Invalid mode. Use '1v1' or '2v2'." }, 400, ct);
            return;
        }

        try
        {
            await Json(await Resolve<ILeaderboardService>().WebBoardAsync(mode, Character(), ct), 200, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Logger.LogError("Error in GET /api/leaderboard: {Error}", e.Message);
            await Json(new JsonObject { ["error"] = "Error fetching leaderboard" }, 500, ct);
        }
    }

    private string? Character() => HttpContext.Request.Query["character"] is { Count: 1 } q && q[0] is { Length: > 0 } c && c != "global" ? c : null;

    private Task Json(JsonObject body, int status, CancellationToken ct)
    {
        HttpContext.Response.StatusCode = status;
        HttpContext.Response.ContentType = "application/json; charset=utf-8";
        return HttpContext.Response.WriteAsync(Js.Stringify(body), ct);
    }
}

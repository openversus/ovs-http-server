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
/// GET /api/leaderboard/{mode}/me?character=: the row of the account at the browser's IP (one account, or the picked
/// one; else {error: "Not connected to game"}), <see cref="ILeaderboardService.WebRankAsync"/>; {error} with no game.
/// 400 for a bad mode, 500 when the read fails, as the TS route.
/// Seen in: TS server: GET /api/leaderboard/{mode}/me.
/// </summary>
public sealed class GetApiLeaderboardByModeMe : EndpointWithoutRequest
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/api/leaderboard/{mode}/me");
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
            if (Resolve<IServiceProvider>().GetService<IMongoDatabase>() is not { } mongo || Resolve<IServiceProvider>().GetService<IConnectionMultiplexer>() is not { } redis
                || await WebAccounts.PickedAsync(HttpContext, mongo, redis.GetDatabase()) is not { } player)
            {
                await Json(new JsonObject { ["error"] = "Not connected to game" }, 200, ct);
                return;
            }

            string? character = HttpContext.Request.Query["character"] is { Count: 1 } q && q[0] is { Length: > 0 } c && c != "global" ? c : null;
            var rank = await Resolve<ILeaderboardService>().WebRankAsync(player["_id"].ToString()!, mode, character, ct);
            await Json(rank ?? new JsonObject { ["error"] = character is null ? "No ranked games played" : $"No games on {character}" }, 200, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Logger.LogError("Error in GET /api/leaderboard/me: {Error}", e.Message);
            await Json(new JsonObject { ["error"] = "Error fetching player rank" }, 500, ct);
        }
    }

    private Task Json(JsonObject body, int status, CancellationToken ct)
    {
        HttpContext.Response.StatusCode = status;
        HttpContext.Response.ContentType = "application/json; charset=utf-8";
        return HttpContext.Response.WriteAsync(Js.Stringify(body), ct);
    }
}

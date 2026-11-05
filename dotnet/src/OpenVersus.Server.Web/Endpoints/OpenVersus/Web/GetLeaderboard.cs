using System.Text.Json.Nodes;
using FastEndpoints;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Web.Site;
using StackExchange.Redis;

namespace OpenVersus.Server.Web.Endpoints.OpenVersus.Web;

/// <summary>
/// GET /leaderboard: the leaderboard page (leaderboard.html; the browser asks /api/leaderboard/{mode}). TS server: GET /leaderboard.
/// Seen in: TS server: GET /leaderboard.
/// </summary>
public sealed class GetLeaderboard : EndpointWithoutRequest
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/leaderboard");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        await Pages.SendAsync(HttpContext, Pages.Leaderboard());
    }
}

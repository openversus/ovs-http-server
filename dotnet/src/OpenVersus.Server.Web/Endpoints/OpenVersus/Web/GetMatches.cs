using System.Text.Json.Nodes;
using FastEndpoints;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Web.Site;
using StackExchange.Redis;

namespace OpenVersus.Server.Web.Endpoints.OpenVersus.Web;

/// <summary>
/// GET /matches: the live matches page (matches.html; the browser polls /api/matches). TS server: GET /matches.
/// Seen in: TS server: GET /matches.
/// </summary>
public sealed class GetMatches : EndpointWithoutRequest
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/matches");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        await Pages.SendAsync(HttpContext, Pages.Matches());
    }
}

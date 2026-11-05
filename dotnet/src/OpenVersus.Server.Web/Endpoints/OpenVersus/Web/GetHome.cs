using System.Text.Json.Nodes;
using FastEndpoints;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Web.Site;
using StackExchange.Redis;

namespace OpenVersus.Server.Web.Endpoints.OpenVersus.Web;

/// <summary>
/// GET /home: the site's home page (home.html, no variables). TS server: GET /home.
/// Seen in: TS server: GET /home.
/// </summary>
public sealed class GetHome : EndpointWithoutRequest
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/home");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        await Pages.SendAsync(HttpContext, Pages.Home());
    }
}

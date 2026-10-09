using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Compat;
using FastEndpoints;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Web.Site;
using StackExchange.Redis;

namespace OpenVersus.Server.Web.Endpoints.OpenVersus.Web;

/// <summary>
/// GET /api/matches: the shared live-matches snapshot (<see cref="LiveMatches"/>, at most one refresh every 2 s, on request). TS server: GET /api/matches.
/// Seen in: TS server: GET /api/matches.
/// </summary>
public sealed class GetApiMatches : EndpointWithoutRequest
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/api/matches");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        HttpContext.Response.ContentType = "application/json; charset=utf-8";
        await HttpContext.Response.WriteAsync((await Resolve<LiveMatches>().SnapshotAsync(ct)).ToJsonString(), ct);
    }
}

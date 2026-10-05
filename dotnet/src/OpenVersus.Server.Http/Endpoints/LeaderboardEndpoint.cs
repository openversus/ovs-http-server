using FastEndpoints;
using OpenVersus.Server.Core.Leaderboards;
using OpenVersus.Server.Http.Shared.Hosting;
using HydraCodec = OpenVersus.Server.Core.Hydra.Hydra;

namespace OpenVersus.Server.Http.Endpoints;

/// <summary>
/// A leaderboard screen (<see cref="ILeaderboardService"/>): answered in Hydra whatever the request's content type, as the
/// TS server encodes these itself.
/// </summary>
public abstract class LeaderboardEndpoint : EndpointWithoutRequest
{
    /// <summary>The query as Express reads it: count (a repeated one joined with commas), fields and account_fields.</summary>
    protected LeaderboardQuery Query()
    {
        var query = HttpContext.Request.Query;
        return new LeaderboardQuery(
            query.TryGetValue("count", out var count) ? string.Join(",", count.ToArray()) : null,
            query["fields"].Where(f => f is not null).ToArray()!,
            query["account_fields"].Where(f => f is not null).ToArray()!);
    }

    protected Task SendLeaderboardAsync(System.Text.Json.Nodes.JsonObject body, CancellationToken ct) =>
        HydraBodies.WriteEncodedAsync(HttpContext, HydraCodec.Encode(body), ct);
}

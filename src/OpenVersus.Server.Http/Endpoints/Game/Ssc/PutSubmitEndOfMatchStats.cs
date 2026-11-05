using System.Text.Json.Nodes;
using FastEndpoints;
using OpenVersus.Server.Core.Matches;
using OpenVersus.Server.Http.Shared.Endpoints;
using OpenVersus.Server.Http.Shared.Hosting;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/submit_end_of_match_stats {ContainerMatchId, EndOfMatchStats, MatchLength}: a game's end as the
/// player's game simulated it (<see cref="IMatchResults"/>). Answers the ranked payload for the player's mode, or
/// {body: {}} with no session or match.
/// Seen in: binary ssc name; captured 9x; TS server: PUT /ssc/invoke/submit_end_of_match_stats.
/// Ssc: server/capture.
/// </summary>
public sealed class PutSubmitEndOfMatchStats : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/submit_end_of_match_stats");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var body = await ReadBodyAsync(ct) as JsonObject ?? [];
        string? player = HttpContext.Session() is { AccountId.Length: > 0 } session ? session.AccountId : null;
        var answer = await Resolve<IMatchResults>().SubmitAsync(player, body, ct);
        await SendJsonAsync(new JsonObject { ["body"] = answer, ["metadata"] = null, ["return_code"] = 0 }, ct);
    }
}

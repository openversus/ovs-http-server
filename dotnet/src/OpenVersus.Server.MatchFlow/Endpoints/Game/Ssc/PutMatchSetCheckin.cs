using System.Text.Json.Nodes;
using FastEndpoints;
using OpenVersus.Server.Core.Matches;
using OpenVersus.Server.Http.Shared.Endpoints;
using OpenVersus.Server.Http.Shared.Hosting;

namespace OpenVersus.Server.MatchFlow.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/match_set_checkin {ContainerMatchId}: the player is ready for the set's next game after that one
/// (<see cref="IRankedSets"/>).
/// Answers {body: {}} whatever happened, as the TS server did.
/// Seen in: binary ssc name; captured 6x; TS server: PUT /ssc/invoke/match_set_checkin.
/// Ssc: server/capture.
/// </summary>
public sealed class PutMatchSetCheckin : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/match_set_checkin");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var body = await ReadBodyAsync(ct) as JsonObject;
        if (HttpContext.Session() is { AccountId.Length: > 0 } session)
        {
            Logger.LogInformation("match_set_checkin from player {Player}", session.AccountId);
            string? match = body?["ContainerMatchId"] is JsonValue v && v.TryGetValue(out string? id) ? id : null;
            await Resolve<IRankedSets>().CheckinAsync(session.AccountId, match);
        }
        else
        {
            Logger.LogWarning("match_set_checkin with no session; answered empty");
        }

        await SendJsonAsync(new JsonObject { ["body"] = new JsonObject(), ["metadata"] = null, ["return_code"] = 0 }, ct);
    }
}

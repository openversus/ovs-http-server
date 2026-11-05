using System.Text.Json.Nodes;
using FastEndpoints;
using OpenVersus.Server.Core.Matches;
using OpenVersus.Server.Http.Shared.Endpoints;
using OpenVersus.Server.Http.Shared.Hosting;

namespace OpenVersus.Server.MatchFlow.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/match_set_absent {ContainerMatchId}: the player's check-in timer ran out (sent after a check-in too);
/// a check-in all the same (<see cref="IRankedSets"/>).
/// Answers {body: {}} whatever happened, as the TS server did.
/// Seen in: binary ssc name; captured 2x; TS server: PUT /ssc/invoke/match_set_absent.
/// Ssc: server/capture.
/// </summary>
public sealed class PutMatchSetAbsent : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/match_set_absent");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var body = await ReadBodyAsync(ct) as JsonObject;
        if (HttpContext.Session() is { AccountId.Length: > 0 } session)
        {
            Logger.LogInformation("match_set_absent from player {Player} (treating as auto-ready)", session.AccountId);
            string? match = body?["ContainerMatchId"] is JsonValue v && v.TryGetValue(out string? id) ? id : null;
            await Resolve<IRankedSets>().CheckinAsync(session.AccountId, match);
        }
        else
        {
            Logger.LogWarning("match_set_absent with no session; answered empty");
        }

        await SendJsonAsync(new JsonObject { ["body"] = new JsonObject(), ["metadata"] = null, ["return_code"] = 0 }, ct);
    }
}

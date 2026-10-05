using System.Text.Json.Nodes;
using FastEndpoints;
using OpenVersus.Server.Core.Matches;
using OpenVersus.Server.Http.Shared.Endpoints;
using OpenVersus.Server.Http.Shared.Hosting;

namespace OpenVersus.Server.MatchFlow.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/match_set_concede {ContainerMatchId}: the player's team gives up the set (<see cref="IRankedSets"/>; the
/// body is not read, as there).
/// Answers {body: {}} whatever happened, as the TS server did.
/// Seen in: binary ssc name; TS server: PUT /ssc/invoke/match_set_concede.
/// Ssc: server/capture.
/// </summary>
public sealed class PutMatchSetConcede : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/match_set_concede");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (HttpContext.Session() is { AccountId.Length: > 0 } session)
        {
            Logger.LogInformation("match_set_concede from player {Player}", session.AccountId);
            await Resolve<IRankedSets>().ConcedeAsync(session.AccountId);
        }
        else
        {
            Logger.LogWarning("match_set_concede with no session; answered empty");
        }

        await SendJsonAsync(new JsonObject { ["body"] = new JsonObject(), ["metadata"] = null, ["return_code"] = 0 }, ct);
    }
}

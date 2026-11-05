using System.Text.Json.Nodes;
using FastEndpoints;
using OpenVersus.Server.Http.Shared.Endpoints;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// GET /ssc/invoke/load_gameplay_config?MatchId=... (the TS server's handler): an empty success, {body: {}, metadata:
/// null, return_code: 200}. The match's gameplay config reaches the game over the websocket (the match-found push),
/// not here; the TS server logged the match id and answered nothing else, and so does this.
/// Seen in: server (GET), binary ssc name (probable).
/// </summary>
public sealed class GetLoadGameplayConfig : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/ssc/invoke/load_gameplay_config");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        Logger.LogInformation("load_gameplay_config for match {MatchId}: the config went over the websocket; empty success", (string?)HttpContext.Request.Query["MatchId"] ?? "(none)");
        await SendJsonAsync(new JsonObject { ["body"] = new JsonObject(), ["metadata"] = null, ["return_code"] = 200 }, ct);
    }
}

using System.Text.Json.Nodes;
using FastEndpoints;
using OpenVersus.Server.Core.Matches;
using OpenVersus.Server.Http.Shared.Endpoints;
using OpenVersus.Server.Http.Shared.Hosting;

namespace OpenVersus.Server.MatchFlow.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/toast_player {ContainerMatchId, ToasteeId}: one player toasts another after a match
/// (<see cref="IMatchToasts"/>). Answers {body: {}} whatever happened, as the TS server did (with no session too: there
/// it was never answered).
/// Seen in: binary ssc name; captured 4x; TS server: PUT /ssc/invoke/toast_player.
/// Ssc: server/capture.
/// </summary>
public sealed class PutToastPlayer : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/toast_player");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var session = HttpContext.Session();
        string username = session?.Claims["username"] is JsonValue v && v.TryGetValue(out string? name) ? name : "";
        await Resolve<IMatchToasts>().ToastAsync(session?.AccountId ?? "", username, await ReadBodyAsync(ct) as JsonObject ?? [], ct);
        await SendJsonAsync(new JsonObject { ["body"] = new JsonObject(), ["metadata"] = null, ["return_code"] = 0 }, ct);
    }
}

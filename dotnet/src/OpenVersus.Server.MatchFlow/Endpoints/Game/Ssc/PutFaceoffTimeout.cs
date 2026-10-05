using System.Text.Json.Nodes;
using FastEndpoints;
using OpenVersus.Server.Core.Matches;
using OpenVersus.Server.Http.Shared.Endpoints;
using OpenVersus.Server.Http.Shared.Hosting;

namespace OpenVersus.Server.MatchFlow.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/faceoff_timeout: an opponent never loaded into the match; the ranked set is dropped, unrated
/// (<see cref="IRankedSets"/>).
/// Answers {body: {}} whatever happened, as the TS server did.
/// Seen in: binary ssc name; TS server: PUT /ssc/invoke/faceoff_timeout.
/// Ssc: server/capture.
/// </summary>
public sealed class PutFaceoffTimeout : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/faceoff_timeout");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (HttpContext.Session() is { AccountId.Length: > 0 } session)
        {
            Logger.LogInformation("faceoff_timeout from player {Player}", session.AccountId);
            await Resolve<IRankedSets>().FaceoffTimeoutAsync(session.AccountId);
        }
        else
        {
            Logger.LogWarning("faceoff_timeout with no session; answered empty");
        }

        await SendJsonAsync(new JsonObject { ["body"] = new JsonObject(), ["metadata"] = null, ["return_code"] = 0 }, ct);
    }
}

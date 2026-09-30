using System.Text.Json.Nodes;
using FastEndpoints;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// POST /ssc/invoke/claim_mission_rewards: no mission rewards are granted: empty MissionControllerContainers and ClaimLocks, as the TS server answers (handlers/ssc.ts) and every captured answer was.
/// Seen in: binary ssc name; captured 3x; TS server: POST /ssc/invoke/claim_mission_rewards.
/// Follow-up: docs/SSC.md (what the game sends, and what is left to do).
/// </summary>
public sealed class PostClaimMissionRewards : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.POST);
        Routes("/ssc/invoke/claim_mission_rewards");
    }

    public override Task HandleAsync(CancellationToken ct) =>
        SendJsonAsync(new JsonObject
        {
            ["body"] = new JsonObject { ["MissionControllerContainers"] = new JsonObject(), ["ClaimLocks"] = new JsonObject() },
            ["metadata"] = null,
            ["return_code"] = 0,
        }, ct);
}

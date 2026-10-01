using System.Text.Json.Nodes;
using FastEndpoints;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using OpenVersus.Server.Core.Missions;
using OpenVersus.Server.Http.Hosting;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// POST /ssc/invoke/claim_mission_rewards: with Missions:Enabled, claims the finished missions named and answers the
/// player's missions after it (<see cref="IMissionService.ClaimAsync"/>); off, nothing is granted: empty
/// MissionControllerContainers and ClaimLocks, as the TS server answers (handlers/ssc.ts) and every captured answer was.
/// Seen in: binary ssc name; captured 3x; TS server: POST /ssc/invoke/claim_mission_rewards.
/// Follow-up: docs/SSC.md, docs/MISSIONS.md.
/// </summary>
public sealed class PostClaimMissionRewards : JsonBodyEndpoint
{
    private static JsonObject Nothing() => new()
    {
        ["body"] = new JsonObject { ["MissionControllerContainers"] = new JsonObject(), ["ClaimLocks"] = new JsonObject() },
        ["metadata"] = null,
        ["return_code"] = 0,
    };

    public override void Configure()
    {
        Verbs(FastEndpoints.Http.POST);
        Routes("/ssc/invoke/claim_mission_rewards");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!Resolve<IOptionsMonitor<MissionSettings>>().CurrentValue.Enabled)
        {
            await SendJsonAsync(Nothing(), ct);
            return;
        }

        var body = await ReadBodyAsync(ct) as JsonObject ?? [];
        var answer = Nothing();
        try
        {
            answer = await Resolve<IMissionService>().ClaimAsync(HttpContext.Session()?.AccountId ?? "", body, ct);
        }
        catch (Exception e) when (e is InvalidOperationException or MongoException or TimeoutException)
        {
            Logger.LogError("Mission claim could not be made: answering nothing claimed ({Error})", e.Message);
        }

        await SendJsonAsync(answer, ct);
    }
}

using FastEndpoints;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using OpenVersus.Server.Core.Missions;
using OpenVersus.Server.Http.Shared.Hosting;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// POST /ssc/invoke/get_or_create_mission_object: the player's mission object (<see cref="IMissionService"/>) with
/// Missions:Enabled; off, the TS server's answer, no containers (<see cref="MissionObject"/>). The game sends {}.
/// Seen in: binary ssc name; captured 11x; TS server: POST /ssc/invoke/get_or_create_mission_object.
/// Follow-up: docs/MISSIONS.md (real per-player missions).
/// </summary>
public sealed class PostGetOrCreateMissionObject : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.POST);
        Routes("/ssc/invoke/get_or_create_mission_object");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        string? accountId = HttpContext.Session()?.AccountId;
        var answer = MissionObject.Answer(accountId, enabled: false);
        if (Resolve<IOptionsMonitor<MissionSettings>>().CurrentValue.Enabled)
        {
            try
            {
                answer = await Resolve<IMissionService>().GetOrCreateAsync(accountId ?? "", ct);
            }
            catch (Exception e) when (e is InvalidOperationException or MongoException or TimeoutException)
            {
                // The game waits on this answer: no missions rather than none at all.
                Logger.LogError("Missions for {Account} could not be read: answering none ({Error})", accountId, e.Message);
            }
        }

        await SendJsonAsync(answer, ct);
    }
}

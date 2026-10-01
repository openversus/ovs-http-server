using FastEndpoints;
using Microsoft.Extensions.Options;
using OpenVersus.Server.Core.Missions;
using OpenVersus.Server.Http.Hosting;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// POST /ssc/invoke/get_or_create_mission_object: the player's mission object, the TS server's fixed one with no
/// containers unless Missions:Enabled (see <see cref="MissionObject"/>). The game sends {}.
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

    public override Task HandleAsync(CancellationToken ct) =>
        SendJsonAsync(MissionObject.Answer(HttpContext.Session()?.AccountId, Resolve<IOptionsMonitor<MissionSettings>>().CurrentValue.Enabled), ct);
}

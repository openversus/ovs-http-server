using System.Text.Json.Nodes;
using FastEndpoints;
using OpenVersus.Server.Core.Leaderboards;
using OpenVersus.Server.Http.Shared.Endpoints;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// GET /ssc/invoke/get_gm_leaderboards: the Grandmaster leaderboards, the 100 players with the best Master-rated fighter
/// in each mode, each once (<see cref="ILeaderboardService.GmLeaderboardsAsync"/>; the tier is defined by this list), in the SSC
/// envelope, return_code 0; empty lists when a read fails, as the TS handler's fallback answered.
/// Seen in: server (GET), binary ssc name.
/// </summary>
public sealed class GetGetGmLeaderboards : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/ssc/invoke/get_gm_leaderboards");
    }

    public override async Task HandleAsync(CancellationToken ct) =>
        await SendJsonAsync(new JsonObject { ["body"] = await Resolve<ILeaderboardService>().GmLeaderboardsAsync(ct), ["metadata"] = null, ["return_code"] = 0 }, ct);
}

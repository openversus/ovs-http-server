using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// POST /ssc/invoke/claim_mission_rewards.
/// Seen in: binary ssc name; captured 3x; TS server: POST /ssc/invoke/claim_mission_rewards.
/// Ssc: server/capture.
/// </summary>
public sealed class PostClaimMissionRewards : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.POST);
        Routes("/ssc/invoke/claim_mission_rewards");
    }
}

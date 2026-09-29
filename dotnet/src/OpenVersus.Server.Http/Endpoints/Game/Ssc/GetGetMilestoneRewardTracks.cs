using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// GET /ssc/invoke/get_milestone_reward_tracks.
/// Seen in: binary ssc name; TS server: GET /ssc/invoke/get_milestone_reward_tracks.
/// Ssc: binary (probable).
/// </summary>
public sealed class GetGetMilestoneRewardTracks : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/ssc/invoke/get_milestone_reward_tracks");
    }
}

using FastEndpoints;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// GET /ssc/invoke/get_milestone_reward_tracks: the reward tracks' states, the same for everyone, as the TS server
/// answers (Static/ssc-get-milestone-reward-tracks.json). In the login batch.
/// Seen in: binary ssc name; TS server: GET /ssc/invoke/get_milestone_reward_tracks.
/// </summary>
public sealed class GetGetMilestoneRewardTracks : StaticEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/ssc/invoke/get_milestone_reward_tracks");
    }

    public override Task HandleAsync(CancellationToken ct) => SendStaticAsync("ssc-get-milestone-reward-tracks", ct);
}

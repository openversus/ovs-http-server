using FastEndpoints;
using Microsoft.Extensions.Options;
using OpenVersus.Server.Core.RewardTracks;
using OpenVersus.Server.Http.Shared.Hosting;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// GET /ssc/invoke/get_milestone_reward_tracks: the player's reward tracks (<see cref="IRewardTrackService"/>) with
/// RewardTracks:PerPlayer or CharacterMastery; both off, the same for everyone, as the TS server answers
/// (Static/ssc-get-milestone-reward-tracks.json).
/// In the login batch.
/// Seen in: binary ssc name; TS server: GET /ssc/invoke/get_milestone_reward_tracks.
/// </summary>
public sealed class GetGetMilestoneRewardTracks : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/ssc/invoke/get_milestone_reward_tracks");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!Resolve<IOptionsMonitor<RewardTrackSettings>>().CurrentValue.Any)
        {
            await SendStaticAsync("ssc-get-milestone-reward-tracks", ct);
            return;
        }

        await SendJsonAsync(await Resolve<IRewardTrackService>().AnswerAsync(HttpContext.Session()?.AccountId ?? "", ct), ct);
    }
}

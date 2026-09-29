using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// Any method /ssc/invoke/local_leaderboard_claim_rewards.
/// Seen in: binary ssc name.
/// Ssc: binary (probable).
/// </summary>
public sealed class AnyLocalLeaderboardClaimRewards : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET, FastEndpoints.Http.PUT, FastEndpoints.Http.POST, FastEndpoints.Http.DELETE);
        Routes("/ssc/invoke/local_leaderboard_claim_rewards");
    }
}

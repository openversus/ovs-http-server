using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// Any method /ssc/invoke/local_leaderboard_has_unclaimed_rewards.
/// Seen in: binary ssc name.
/// Ssc: binary (probable).
/// </summary>
public sealed class AnyLocalLeaderboardHasUnclaimedRewards : TsCatchAllEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET, FastEndpoints.Http.PUT, FastEndpoints.Http.POST, FastEndpoints.Http.DELETE);
        Routes("/ssc/invoke/local_leaderboard_has_unclaimed_rewards");
    }
}

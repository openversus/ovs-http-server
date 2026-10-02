using FastEndpoints;
using OpenVersus.Server.Core.Leaderboards;
using OpenVersus.Server.Http.Shared.Hosting;

namespace OpenVersus.Server.Http.Endpoints.Game.Leaderboards;

/// <summary>
/// GET /leaderboards/{id}/around/me: "me" is taken as a player id, as the TS server's route does, so no rows.
/// Seen in: binary 0x145066ac0; TS server: GET /leaderboards/{slug}/around/{playerId}.
/// </summary>
[NoHydraToken]
public sealed class GetLeaderboardsByIdAroundMe : LeaderboardEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/leaderboards/{id}/around/me");
    }

    public override async Task HandleAsync(CancellationToken ct) =>
        await SendLeaderboardAsync(await Resolve<ILeaderboardService>().AroundAsync(Route<string>("id")!, "me", Query(), ct), ct);
}

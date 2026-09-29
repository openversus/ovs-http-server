using FastEndpoints;
using OpenVersus.Server.Core.Leaderboards;
using OpenVersus.Server.Http.Hosting;

namespace OpenVersus.Server.Http.Endpoints.Game.Leaderboards;

/// <summary>
/// GET /leaderboards/{id}/around/{account}: the rows around the player's place (see <see cref="ILeaderboardService"/>).
/// Seen in: binary 0x1450668b0; captured 4x; TS server: GET /leaderboards/{slug}/around/{playerId}.
/// </summary>
[NoHydraToken]
public sealed class GetLeaderboardsByIdAroundByAccount : LeaderboardEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/leaderboards/{id}/around/{account}");
    }

    public override async Task HandleAsync(CancellationToken ct) =>
        await SendLeaderboardAsync(await Resolve<ILeaderboardService>().AroundAsync(Route<string>("id")!, Route<string>("account")!, Query(), ct), ct);
}

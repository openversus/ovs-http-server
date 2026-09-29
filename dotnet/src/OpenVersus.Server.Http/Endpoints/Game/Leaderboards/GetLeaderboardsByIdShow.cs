using FastEndpoints;
using OpenVersus.Server.Core.Leaderboards;
using OpenVersus.Server.Http.Hosting;

namespace OpenVersus.Server.Http.Endpoints.Game.Leaderboards;

/// <summary>
/// GET /leaderboards/{id}/show: the top of the board (count, at most 100; see <see cref="ILeaderboardService"/>).
/// Seen in: binary 0x1450666c0; captured 2x; TS server: GET /leaderboards/{slug}/show.
/// </summary>
[NoHydraToken]
public sealed class GetLeaderboardsByIdShow : LeaderboardEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/leaderboards/{id}/show");
    }

    public override async Task HandleAsync(CancellationToken ct) =>
        await SendLeaderboardAsync(await Resolve<ILeaderboardService>().ShowAsync(Route<string>("id")!, Query(), ct), ct);
}

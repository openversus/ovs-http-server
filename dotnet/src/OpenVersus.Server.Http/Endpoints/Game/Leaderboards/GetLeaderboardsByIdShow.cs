using FastEndpoints;
using OpenVersus.Server.Http.Hosting;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Leaderboards;

/// <summary>
/// GET /leaderboards/{id}/show.
/// Seen in: binary 0x1450666c0; captured 2x; TS server: GET /leaderboards/{slug}/show.
/// </summary>
[NoHydraToken]
public sealed class GetLeaderboardsByIdShow : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/leaderboards/{id}/show");
    }
}

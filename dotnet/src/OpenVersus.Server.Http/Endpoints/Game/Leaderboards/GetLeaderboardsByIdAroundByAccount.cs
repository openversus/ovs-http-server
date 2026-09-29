using FastEndpoints;
using OpenVersus.Server.Http.Hosting;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Leaderboards;

/// <summary>
/// GET /leaderboards/{id}/around/{account}.
/// Seen in: binary 0x1450668b0; captured 4x; TS server: GET /leaderboards/{slug}/around/{playerId}.
/// </summary>
[NoHydraToken]
public sealed class GetLeaderboardsByIdAroundByAccount : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/leaderboards/{id}/around/{account}");
    }
}

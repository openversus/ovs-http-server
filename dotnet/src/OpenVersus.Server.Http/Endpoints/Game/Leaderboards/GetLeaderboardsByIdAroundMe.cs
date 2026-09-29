using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Leaderboards;

/// <summary>
/// GET /leaderboards/{id}/around/me.
/// Seen in: binary 0x145066ac0.
/// </summary>
public sealed class GetLeaderboardsByIdAroundMe : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/leaderboards/{id}/around/me");
    }
}

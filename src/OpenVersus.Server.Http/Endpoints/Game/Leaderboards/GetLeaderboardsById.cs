using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Leaderboards;

/// <summary>
/// GET /leaderboards/{id}.
/// Seen in: binary 0x145065df0.
/// </summary>
public sealed class GetLeaderboardsById : TsCatchAllEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/leaderboards/{id}");
    }
}

using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Leaderboards;

/// <summary>
/// GET /leaderboards/{id}/friends.
/// Seen in: binary 0x145066cb0.
/// </summary>
public sealed class GetLeaderboardsByIdFriends : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/leaderboards/{id}/friends");
    }
}

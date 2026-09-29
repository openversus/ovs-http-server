using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Friends;

/// <summary>
/// GET /friends/me.
/// Seen in: binary 0x140f9a310; captured 14x; TS server: GET /friends/me.
/// Social layer; method from capture.
/// </summary>
public sealed class GetFriendsMe : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/friends/me");
    }
}

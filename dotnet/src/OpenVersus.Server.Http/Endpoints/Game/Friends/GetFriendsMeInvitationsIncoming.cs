using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Friends;

/// <summary>
/// GET /friends/me/invitations/incoming.
/// Seen in: binary 0x140f99fb0; captured 14x; TS server: GET /friends/me/invitations/incoming.
/// Social layer; method from capture.
/// </summary>
public sealed class GetFriendsMeInvitationsIncoming : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/friends/me/invitations/incoming");
    }
}

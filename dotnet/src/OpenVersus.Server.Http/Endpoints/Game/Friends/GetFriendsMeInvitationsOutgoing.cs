using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Friends;

/// <summary>
/// GET /friends/me/invitations/outgoing.
/// Seen in: binary 0x140f9a160; captured 14x; TS server: GET /friends/me/invitations/outgoing.
/// Social layer; method from capture.
/// </summary>
public sealed class GetFriendsMeInvitationsOutgoing : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/friends/me/invitations/outgoing");
    }
}

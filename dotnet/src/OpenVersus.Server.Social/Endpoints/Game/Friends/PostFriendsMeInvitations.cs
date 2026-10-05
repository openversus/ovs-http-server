using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Social.Endpoints.Game.Friends;

/// <summary>
/// POST /friends/me/invitations.
/// Seen in: binary 0x140f9b5e0; TS server: POST /friends/me/invitations.
/// Social layer; method from server.
/// </summary>
public sealed class PostFriendsMeInvitations : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.POST);
        Routes("/friends/me/invitations");
    }
}

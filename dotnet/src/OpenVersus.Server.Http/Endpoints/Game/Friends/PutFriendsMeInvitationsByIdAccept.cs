using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Friends;

/// <summary>
/// PUT /friends/me/invitations/{id}/accept.
/// Seen in: binary 0x140f932a0; TS server: PUT /friends/me/invitations/{id}/accept.
/// Social layer; method from server.
/// </summary>
public sealed class PutFriendsMeInvitationsByIdAccept : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/friends/me/invitations/{id}/accept");
    }
}

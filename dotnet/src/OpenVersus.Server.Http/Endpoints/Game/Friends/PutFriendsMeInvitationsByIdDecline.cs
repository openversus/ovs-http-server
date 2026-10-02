using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Friends;

/// <summary>
/// PUT /friends/me/invitations/{id}/decline.
/// Seen in: binary 0x140f97d00; TS server: PUT /friends/me/invitations/{id}/decline.
/// Social layer; method from server.
/// </summary>
public sealed class PutFriendsMeInvitationsByIdDecline : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/friends/me/invitations/{id}/decline");
    }
}

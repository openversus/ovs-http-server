using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Friends;

/// <summary>
/// Any method /friends/me/invitations/{id}/cancel.
/// Seen in: binary 0x140f943c0.
/// Social layer; method from unknown.
/// </summary>
public sealed class AnyFriendsMeInvitationsByIdCancel : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET, FastEndpoints.Http.PUT, FastEndpoints.Http.POST, FastEndpoints.Http.DELETE);
        Routes("/friends/me/invitations/{id}/cancel");
    }
}

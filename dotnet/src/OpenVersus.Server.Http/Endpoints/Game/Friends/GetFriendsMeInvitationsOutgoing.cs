using FastEndpoints;
using OpenVersus.Server.Core.Friends;

namespace OpenVersus.Server.Http.Endpoints.Game.Friends;

/// <summary>
/// GET /friends/me/invitations/outgoing: pending friend requests from the player (see FriendsService).
/// Seen in: binary 0x140f9a160; captured 14x; TS server: GET /friends/me/invitations/outgoing.
/// </summary>
public sealed class GetFriendsMeInvitationsOutgoing : FriendsPageEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/friends/me/invitations/outgoing");
    }

    protected override Task<FriendsPage> ReadAsync(IFriendsService friends, string? accountId, CancellationToken ct) => friends.OutgoingAsync(accountId, ct);
}

using FastEndpoints;
using OpenVersus.Server.Core.Friends;

namespace OpenVersus.Server.Http.Endpoints.Game.Friends;

/// <summary>
/// GET /friends/me/invitations/incoming: pending friend requests to the player (see FriendsService).
/// Seen in: binary 0x140f99fb0; captured 14x; TS server: GET /friends/me/invitations/incoming.
/// </summary>
public sealed class GetFriendsMeInvitationsIncoming : FriendsPageEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/friends/me/invitations/incoming");
    }

    protected override Task<FriendsPage> ReadAsync(IFriendsService friends, string? accountId, CancellationToken ct) => friends.IncomingAsync(accountId, ct);
}

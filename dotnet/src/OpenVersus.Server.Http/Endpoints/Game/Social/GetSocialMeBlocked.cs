using FastEndpoints;
using OpenVersus.Server.Core.Friends;

namespace OpenVersus.Server.Http.Endpoints.Game.Social;

/// <summary>
/// GET /social/me/blocked: the players the player blocked (see FriendsService).
/// Seen in: binary 0x140f99e00; captured 14x; TS server: GET /social/me/blocked.
/// </summary>
public sealed class GetSocialMeBlocked : FriendsPageEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/social/me/blocked");
    }

    protected override Task<FriendsPage> ReadAsync(IFriendsService friends, string? accountId, CancellationToken ct) => friends.BlockedAsync(accountId, ct);
}

using FastEndpoints;
using OpenVersus.Server.Core.Friends;

namespace OpenVersus.Server.Social.Endpoints.Game.Friends;

/// <summary>
/// GET /friends/me: the player's friends; page_size in the query is not read, as there (see FriendsService).
/// Seen in: binary 0x140f9a310; captured 14x; TS server: GET /friends/me.
/// </summary>
public sealed class GetFriendsMe : FriendsPageEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/friends/me");
    }

    protected override Task<FriendsPage> ReadAsync(IFriendsService friends, string? accountId, CancellationToken ct) => friends.FriendsAsync(accountId, ct);
}

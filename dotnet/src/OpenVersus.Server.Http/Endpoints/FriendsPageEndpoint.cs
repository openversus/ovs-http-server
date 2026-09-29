using FastEndpoints;
using OpenVersus.Server.Core.Friends;
using OpenVersus.Server.Http.Hosting;

namespace OpenVersus.Server.Http.Endpoints;

/// <summary>
/// A friends read: the session's account, answered with 200 and JSON as the TS server writes it (Hydra for a Hydra
/// request), whatever happened.
/// </summary>
public abstract class FriendsPageEndpoint : EndpointWithoutRequest
{
    protected abstract Task<FriendsPage> ReadAsync(IFriendsService friends, string? accountId, CancellationToken ct);

    public override async Task HandleAsync(CancellationToken ct)
    {
        var page = await ReadAsync(Resolve<IFriendsService>(), HttpContext.Session()?.AccountId, ct);
        await Send.StringAsync(page.Json, contentType: "application/json; charset=utf-8", cancellation: ct);
    }
}

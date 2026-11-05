using OpenVersus.Server.Core.Matches;
using OpenVersus.Server.Http.Shared.Endpoints;

namespace OpenVersus.Server.MatchFlow.Endpoints.OpenVersus.Rollback;

/// <summary>
/// A rollback server's or P2P node's call about a match (<see cref="IRollbackCallbacks"/>). "Nothing for you" is
/// answered as the TS server's res.send(""): 200, empty, text/html; a rollback server ends its match on it, a node gives up.
/// </summary>
public abstract class RollbackEndpoint : JsonBodyEndpoint
{
    protected Task SendEmptyAsync(CancellationToken ct) => Send.StringAsync("", contentType: "text/html; charset=utf-8", cancellation: ct);

    /// <summary>Sends <paramref name="registration"/> (signed when it has a signature), and only once it is sent tells
    /// its players to connect, as the TS server answered before it published; "" without one.</summary>
    protected async Task SendRegistrationAsync(IRollbackCallbacks callbacks, RollbackRegistration? registration, CancellationToken ct)
    {
        if (registration is null)
        {
            await SendEmptyAsync(ct);
            return;
        }

        if (registration.Signature is { } signature)
        {
            HttpContext.Response.Headers[INodeConfig.SignatureHeader] = signature;
        }

        await Send.BytesAsync(registration.Body, contentType: "application/json; charset=utf-8", cancellation: ct);
        await HttpContext.Response.CompleteAsync();
        if (registration.Ready is { } ready)
        {
            await callbacks.ReleaseAsync(ready);
        }
    }
}

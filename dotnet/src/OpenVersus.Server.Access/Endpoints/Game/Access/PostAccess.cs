using OpenVersus.Server.Identity.Epic;
using System.Text.Json.Nodes;
using System.Text.Json;
using FastEndpoints;
using OpenVersus.Server.Core.Access;
using OpenVersus.Server.Core.Hosting;
using OpenVersus.Server.Http.Shared.Hosting;

namespace OpenVersus.Server.Access.Endpoints.Game.Access;

/// <summary>
/// POST /access: the game's login. Seen in: binary 0x144f9f5b0; captured 11x; TS server: POST /access.
/// Of the body (auth, metadata, options) only auth.epic is read: the Epic Games Store build's login carries Epic's
/// access token for the account (a JWT Epic signs for the game's client id, type epic_id, two hours), which the Epic
/// ID token verifier judges by the same rules; verified, its subject is the login's proved Epic id, so every Epic
/// client is proved at its login whether or not the OpenVersus client sent an ID token. auth.steam (an encrypted app
/// ticket nobody can read) is not: see AccessService.
/// </summary>
[NoHydraToken]
public sealed class PostAccess : EndpointWithoutRequest
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.POST);
        Routes("/access");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        string ip = ClientAddress.Of(HttpContext, stripMapped: true);
        string? token = HttpContext.Request.Headers["x-hydra-access-token"] is { Count: > 0 } header ? header.ToString() : null;
        string? provedEpicId = await ProvedEpicIdAsync(ip, ct);
        switch (await Resolve<IAccessService>().LoginAsync(ip, token, ct, provedEpicId))
        {
            case AccessResult.Ok ok:
                await HydraBodies.WriteAsync(HttpContext, ok.Response, ct);
                break;
            case AccessResult.Banned:
                // As the TS server: 200 and nothing else.
                await Send.ResultAsync(Results.Ok());
                break;
            default:
                await Send.ResultAsync(Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
                break;
        }
    }

    // The request's Epic credential, judged: the id it proves, or null (none sent, refused, or nothing to judge it with).
    private async Task<string?> ProvedEpicIdAsync(string ip, CancellationToken ct)
    {
        string credential = EpicCredential(await BodyAsync(ct));
        if (credential.Length == 0 || HttpContext.RequestServices.GetService<IEpicIdTokenVerifier>() is not { } verifier)
        {
            return null;
        }

        var log = Resolve<ILogger<PostAccess>>();
        switch (await verifier.CheckAsync(credential, Resolve<TimeProvider>().GetUtcNow(), ct))
        {
            case EpicTokenCheck.Verified ok:
                log.LogInformation("The login from {Ip} carries the game's Epic token for {Epic}: proved", ip, ok.AccountId);
                return ok.AccountId;
            case EpicTokenCheck.Refused refused:
                log.LogWarning("The login from {Ip} carries an Epic token that was refused ({Reason}): no Epic id from it", ip, refused.Reason);
                return null;
            default:
                return null;
        }
    }

    /// <summary>auth.epic of a decoded login body (the Hydra middleware leaves JSON in the request), or "".</summary>
    internal static string EpicCredential(JsonNode? body) =>
        body is JsonObject root && root["auth"] is JsonObject auth && auth["epic"] is JsonValue v && v.GetValueKind() == JsonValueKind.String
            && v.GetValue<string>() is { Length: > 0 and <= EpicIdToken.MaxLength } s ? s : "";

    private async Task<JsonNode?> BodyAsync(CancellationToken ct)
    {
        try
        {
            if (HttpContext.Request.ContentLength == 0)
            {
                return null;
            }

            return await JsonNode.ParseAsync(HttpContext.Request.Body, cancellationToken: ct);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

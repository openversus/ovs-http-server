using System.Text.Json;
using System.Text.Json.Nodes;
using FastEndpoints;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Identity;
using OpenVersus.Server.Web.Site;

namespace OpenVersus.Server.Web.Endpoints.OpenVersus.Client;

/// <summary>
/// POST /api/identify: the OpenVersus client registers its identifiers (Steam id with its session ticket, Epic id,
/// hardware fingerprint, install id, client version, rollback node port) for its IP before the game logs in, and gets
/// the token it sends on its own calls. JSON in and out. TS server: POST /api/identify (server.ts); see
/// <see cref="IIdentifyService"/> for what differs (the Steam id needs the ticket).
/// </summary>
public sealed class PostApiIdentify : EndpointWithoutRequest
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.POST);
        Routes("/api/identify");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        JsonObject body;
        try
        {
            using var reader = new StreamReader(HttpContext.Request.Body);
            string text = await reader.ReadToEndAsync(ct);
            // req.body ?? {}: an object's fields, and nothing from a body that is no object.
            body = text.Trim().Length == 0 ? [] : JsonNode.Parse(text) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            await JsonAsync(new JsonObject { ["error"] = "Invalid JSON" }, StatusCodes.Status400BadRequest, ct);
            return;
        }

        try
        {
            switch (await Resolve<IIdentifyService>().RegisterAsync(WebAccounts.Ip(HttpContext), body, ct))
            {
                case IdentifyResult.Ok ok:
                    await JsonAsync(ok.Response, StatusCodes.Status200OK, ct);
                    break;
                case IdentifyResult.UpdateRequired update:
                    await JsonAsync(update.Response, StatusCodes.Status426UpgradeRequired, ct);
                    break;
                case IdentifyResult.BadRequest bad:
                    await JsonAsync(new JsonObject { ["error"] = bad.Error }, StatusCodes.Status400BadRequest, ct);
                    break;
                default:
                    await JsonAsync(new JsonObject { ["error"] = "Internal error" }, StatusCodes.Status503ServiceUnavailable, ct);
                    break;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Logger.LogError(e, "Error in POST /api/identify");
            await JsonAsync(new JsonObject { ["error"] = "Internal error" }, StatusCodes.Status500InternalServerError, ct);
        }
    }

    private Task JsonAsync(JsonObject body, int status, CancellationToken ct) =>
        Send.StringAsync(Js.Stringify(body), status, "application/json; charset=utf-8", ct);
}

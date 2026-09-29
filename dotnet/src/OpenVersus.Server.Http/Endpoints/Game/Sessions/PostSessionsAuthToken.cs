using System.Text.Json;
using System.Text.Json.Nodes;
using FastEndpoints;
using OpenVersus.Server.Http.Hosting;
using Microsoft.Extensions.Options;
using OpenVersus.Server.Core.Access;

namespace OpenVersus.Server.Http.Endpoints.Game.Sessions;

/// <summary>
/// POST /sessions/auth/token: the WB network SDK trades the /access token (the body's code) for its own. JSON, not
/// Hydra. Seen in: binary 0x140f9be40; captured 11x; TS server: POST /sessions/auth/token (no token check there either).
/// </summary>
[NoHydraToken]
public sealed class PostSessionsAuthToken : EndpointWithoutRequest
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.POST);
        Routes("/sessions/auth/token");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        JsonNode? body = null;
        using var reader = new StreamReader(HttpContext.Request.Body);
        string text = await reader.ReadToEndAsync(ct);
        if (text.Trim().Length > 0)
        {
            try
            {
                body = JsonNode.Parse(text);
            }
            catch (JsonException)
            {
                // Express's JSON parser answers a body it cannot read with 400.
                await Send.ResultAsync(Results.BadRequest());
                return;
            }
        }

        await Send.StringAsync(SessionTokenResponse.Build(body, Resolve<IOptionsMonitor<WbNetworkSettings>>().CurrentValue), contentType: "application/json; charset=utf-8", cancellation: ct);
    }
}

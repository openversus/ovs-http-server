using System.Text.Json;
using System.Text.Json.Nodes;
using FastEndpoints;
using OpenVersus.Server.Core.Compat;

namespace OpenVersus.Server.Http.Endpoints;

/// <summary>An endpoint that reads its JSON body itself (a Hydra body arrives here as JSON) and answers with JSON.</summary>
public abstract class JsonBodyEndpoint : StaticEndpoint
{
    /// <summary>The request body as JSON; null when empty or not JSON.</summary>
    protected async Task<JsonNode?> ReadBodyAsync(CancellationToken ct)
    {
        try
        {
            return await JsonNode.ParseAsync(HttpContext.Request.Body, cancellationToken: ct);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Sends <paramref name="value"/> as the TS server's res.send writes it (Hydra for a Hydra request).</summary>
    protected Task SendJsonAsync(JsonNode value, CancellationToken ct) =>
        Send.StringAsync(Js.Stringify(value), contentType: "application/json; charset=utf-8", cancellation: ct);

    /// <summary>As <see cref="SendJsonAsync(JsonNode, CancellationToken)"/>, with the status <paramref name="status"/>.</summary>
    protected Task SendJsonAsync(JsonNode value, int status, CancellationToken ct) =>
        Send.StringAsync(Js.Stringify(value), status, contentType: "application/json; charset=utf-8", cancellation: ct);
}

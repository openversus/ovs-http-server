using System.Text;
using System.Text.Json.Nodes;
using FastEndpoints;
using OpenVersus.Server.Http.Shared.Endpoints;

namespace OpenVersus.Server.Http.Endpoints.Game.Datarouter;

/// <summary>
/// POST /datarouter/api/v1/public/data/clients: Unreal's DataRouter analytics upload (the engine posting event batches).
/// Nothing is kept; {} as the TS handler answers. TEMPORARY: the whole request (query, headers, body up to 16 KB) is
/// logged, as no capture of one exists yet; take the log out once one has been seen.
/// Seen in: binary 0x140f9b8d0; TS server: POST /datarouter/api/v1/public/data/clients.
/// </summary>
public sealed class PostDatarouterApiV1PublicDataClients : JsonBodyEndpoint
{
    private const int LoggedBodyBytes = 16 * 1024;

    public override void Configure()
    {
        Verbs(FastEndpoints.Http.POST);
        Routes("/datarouter/api/v1/public/data/clients");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var request = HttpContext.Request;
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, ct);
        var bytes = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);
        Logger.LogInformation("DataRouter upload: {Query}; headers {Headers}; body ({Length} bytes, {ContentType}): {Body}",
            request.QueryString.Value ?? "", string.Join("; ", request.Headers.Select(h => $"{h.Key}: {h.Value}")), bytes.Length,
            request.ContentType ?? "(none)", Encoding.UTF8.GetString(bytes[..Math.Min(bytes.Length, LoggedBodyBytes)]));
        await SendJsonAsync(new JsonObject(), ct);
    }
}

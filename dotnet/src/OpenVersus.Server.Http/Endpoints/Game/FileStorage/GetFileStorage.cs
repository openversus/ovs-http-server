using FastEndpoints;
using Microsoft.Extensions.Options;
using OpenVersus.Server.Core.FileStorage;

namespace OpenVersus.Server.Http.Endpoints.Game.FileStorage;

/// <summary>
/// GET /file_storage: the game's file records (<see cref="FileStorageResponses"/>).
/// Seen in: binary 0x14505b5d0; captured 11x; TS server: GET /file_storage.
/// </summary>
public sealed class GetFileStorage : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/file_storage");
    }

    public override Task HandleAsync(CancellationToken ct) =>
        SendJsonAsync(FileStorageResponses.List(AssetsUrl.Of(HttpContext)), ct);
}

/// <summary>This server's /assets/ URL as the request reached it, as the TS server builds it (getAssetDownloadUrl).</summary>
public static class AssetsUrl
{
    public static string Of(HttpContext context)
    {
        // req.get("x-forwarded-proto")?.split(",")[0]?.trim() || req.protocol (no trusted proxy: the connection's own)
        string forwarded = string.Join(", ", context.Request.Headers["X-Forwarded-Proto"].ToArray()).Split(',')[0].Trim();
        string protocol = forwarded.Length > 0 ? forwarded : context.Request.IsHttps ? "https" : "http";
        // req.get("host") || env.GAME_DOMAIN
        string host = context.Request.Headers.Host.ToString();
        if (host.Length == 0)
        {
            host = context.RequestServices.GetRequiredService<IOptionsMonitor<AssetsSettings>>().CurrentValue.Domain;
        }

        return $"{protocol}://{host}/assets/";
    }
}

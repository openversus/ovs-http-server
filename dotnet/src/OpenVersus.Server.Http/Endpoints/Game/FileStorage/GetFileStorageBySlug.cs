using FastEndpoints;
using OpenVersus.Server.Core.FileStorage;

namespace OpenVersus.Server.Http.Endpoints.Game.FileStorage;

/// <summary>
/// GET /file_storage/{slug}: one file record (<see cref="FileStorageResponses"/>) for the slugs the TS server has a route
/// for; any other answers as a stub.
/// Seen in: binary 0x14505b5d0; captured 44x; TS server: GET /file_storage/beginnermode-carousel-keyart, GET /file_storage/beginnermode-carousel-thumbnail, GET /file_storage/harley-rift-s5-keyart ....
/// </summary>
public sealed class GetFileStorageBySlug : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/file_storage/{slug}");
    }

    public override Task HandleAsync(CancellationToken ct) => Route<string>("slug") switch
    {
        FileStorageResponses.UpdateKeyart or FileStorageResponses.UpdateThumbnail =>
            SendJsonAsync(FileStorageResponses.UpdateRecord(Route<string>("slug")!, AssetsUrl.Of(HttpContext)), ct),
        { } slug when FileStorageResponses.StaticSlugs.Contains(slug) => SendStaticAsync($"file-storage-{slug}", ct),
        _ => SendNotPortedAsync(),
    };
}

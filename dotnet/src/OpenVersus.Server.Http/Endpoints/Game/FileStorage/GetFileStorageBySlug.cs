using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.FileStorage;

/// <summary>
/// GET /file_storage/{slug}.
/// Seen in: binary 0x14505b5d0; captured 44x; TS server: GET /file_storage/beginnermode-carousel-keyart, GET /file_storage/beginnermode-carousel-thumbnail, GET /file_storage/harley-rift-s5-keyart ....
/// Method from capture.
/// </summary>
public sealed class GetFileStorageBySlug : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/file_storage/{slug}");
    }
}

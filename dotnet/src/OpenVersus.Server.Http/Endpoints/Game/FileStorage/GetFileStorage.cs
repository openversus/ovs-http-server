using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.FileStorage;

/// <summary>
/// GET /file_storage.
/// Seen in: binary 0x14505b5d0; captured 11x; TS server: GET /file_storage.
/// Method from capture.
/// </summary>
public sealed class GetFileStorage : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/file_storage");
    }
}

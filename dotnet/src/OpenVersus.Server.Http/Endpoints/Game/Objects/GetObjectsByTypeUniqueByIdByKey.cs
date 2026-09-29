using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Objects;

/// <summary>
/// GET /objects/{type}/unique/{id}/{key}.
/// Seen in: binary 0x1450615d0; TS server: GET /objects/preferences/unique/{id}/{id1}.
/// Pieces include a bare '/': one more segment.
/// </summary>
public sealed class GetObjectsByTypeUniqueByIdByKey : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/objects/{type}/unique/{id}/{key}");
    }
}

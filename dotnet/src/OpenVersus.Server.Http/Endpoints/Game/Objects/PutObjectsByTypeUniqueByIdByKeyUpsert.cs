using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Objects;

/// <summary>
/// PUT /objects/{type}/unique/{id}/{key}/upsert.
/// Seen in: binary 0x1450617e0.
/// Pieces include a bare '/': one more segment.
/// </summary>
public sealed class PutObjectsByTypeUniqueByIdByKeyUpsert : TsCatchAllEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/objects/{type}/unique/{id}/{key}/upsert");
    }
}

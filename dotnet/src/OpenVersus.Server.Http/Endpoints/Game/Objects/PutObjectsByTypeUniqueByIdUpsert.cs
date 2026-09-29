using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Objects;

/// <summary>
/// PUT /objects/{type}/unique/{id}/upsert.
/// Seen in: binary 0x145061a20.
/// </summary>
public sealed class PutObjectsByTypeUniqueByIdUpsert : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/objects/{type}/unique/{id}/upsert");
    }
}

using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Arenas;

/// <summary>
/// GET /arenas/{id}/instances.
/// Seen in: binary 0x145053920.
/// </summary>
public sealed class GetArenasByIdInstances : TsCatchAllEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/arenas/{id}/instances");
    }
}

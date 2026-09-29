using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Drives;

/// <summary>
/// PUT /drives/{id}/sync.
/// Seen in: binary 0x144fdd810; captured 11x; TS server: PUT /drives/multiversus/sync.
/// Method from capture (id: multiversus).
/// </summary>
public sealed class PutDrivesByIdSync : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/drives/{id}/sync");
    }
}

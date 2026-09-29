using FastEndpoints;

namespace OpenVersus.Server.Http.Endpoints.Game.Drives;

/// <summary>
/// PUT /drives/{id}/sync: for multiversus, nothing to add or delete (Static/drives-multiversus-sync.json), as the TS
/// server answers; it has no route for another drive. The request body is not read.
/// Seen in: binary 0x144fdd810; captured 11x; TS server: PUT /drives/multiversus/sync.
/// </summary>
public sealed class PutDrivesByIdSync : StaticEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/drives/{id}/sync");
    }

    public override Task HandleAsync(CancellationToken ct) =>
        Route<string>("id") == "multiversus" ? SendStaticAsync("drives-multiversus-sync", ct) : SendNotPortedAsync();
}

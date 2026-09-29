using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Batch;

/// <summary>
/// PUT /batch.
/// Seen in: binary 0x145054730; captured 26x; TS server: PUT /batch.
/// Runs sub-requests.
/// </summary>
public sealed class PutBatch : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/batch");
    }
}

using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/perks_lock.
/// Seen in: binary ssc name; captured 9x; TS server: PUT /ssc/invoke/perks_lock.
/// Ssc: server/capture.
/// </summary>
public sealed class PutPerksLock : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/perks_lock");
    }
}

using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/perks_absent.
/// Seen in: binary ssc name; captured 3x; TS server: PUT /ssc/invoke/perks_absent.
/// Ssc: server/capture.
/// </summary>
public sealed class PutPerksAbsent : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/perks_absent");
    }
}

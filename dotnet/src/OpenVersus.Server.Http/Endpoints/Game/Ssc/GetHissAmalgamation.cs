using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// GET /ssc/invoke/hiss_amalgamation.
/// Seen in: binary ssc name; TS server: GET /ssc/invoke/hiss_amalgamation.
/// Ssc: server/capture.
/// </summary>
public sealed class GetHissAmalgamation : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/ssc/invoke/hiss_amalgamation");
    }
}

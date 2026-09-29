using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/hiss_amalgamation.
/// Seen in: binary ssc name; TS server: PUT /ssc/invoke/hiss_amalgamation.
/// Ssc: server/capture.
/// </summary>
public sealed class PutHissAmalgamation : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/hiss_amalgamation");
    }
}

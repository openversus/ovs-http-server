using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.MatchFlow.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/faceoff_timeout.
/// Seen in: binary ssc name; TS server: PUT /ssc/invoke/faceoff_timeout.
/// Ssc: server/capture.
/// </summary>
public sealed class PutFaceoffTimeout : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/faceoff_timeout");
    }
}

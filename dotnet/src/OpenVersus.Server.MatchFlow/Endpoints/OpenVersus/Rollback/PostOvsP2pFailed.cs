using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.MatchFlow.Endpoints.OpenVersus.Rollback;

/// <summary>
/// POST /ovs_p2p_failed.
/// Seen in: TS server: POST /ovs_p2p_failed.
/// Server only.
/// </summary>
public sealed class PostOvsP2pFailed : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.POST);
        Routes("/ovs_p2p_failed");
    }
}

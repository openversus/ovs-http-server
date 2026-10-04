using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.MatchFlow.Endpoints.OpenVersus.Rollback;

/// <summary>
/// POST /ovs_p2p_ready.
/// Seen in: TS server: POST /ovs_p2p_ready.
/// Server only.
/// </summary>
public sealed class PostOvsP2pReady : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.POST);
        Routes("/ovs_p2p_ready");
    }
}

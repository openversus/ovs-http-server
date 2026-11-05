using FastEndpoints;
using OpenVersus.Server.Core.Matches;

namespace OpenVersus.Server.MatchFlow.Endpoints.OpenVersus.Rollback;

/// <summary>
/// POST /ovs_p2p_ready: a P2P host node serves the match; its players are told to connect (<see cref="IRollbackCallbacks.P2PReadyAsync"/>).
/// Seen in: TS server: POST /ovs_p2p_ready; OVSRollbackNode Node.
/// Server only.
/// </summary>
public sealed class PostOvsP2pReady : RollbackEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.POST);
        Routes("/ovs_p2p_ready");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        await Resolve<IRollbackCallbacks>().P2PReadyAsync(await ReadBodyAsync(ct));
        await SendEmptyAsync(ct);
    }
}

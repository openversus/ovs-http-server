using FastEndpoints;
using OpenVersus.Server.Core.Matches;

namespace OpenVersus.Server.MatchFlow.Endpoints.OpenVersus.Rollback;

/// <summary>
/// POST /ovs_p2p_failed: a P2P node found no direct path; answered with the match's relay {host, port} (<see cref="IRollbackCallbacks.P2PFailedAsync"/>).
/// Seen in: TS server: POST /ovs_p2p_failed; OVSRollbackNode Node (LookUpRelayAsync).
/// Server only.
/// </summary>
public sealed class PostOvsP2pFailed : RollbackEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.POST);
        Routes("/ovs_p2p_failed");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (await Resolve<IRollbackCallbacks>().P2PFailedAsync(await ReadBodyAsync(ct)) is { } relay)
        {
            await SendJsonAsync(relay, ct);
            return;
        }

        await SendEmptyAsync(ct);
    }
}

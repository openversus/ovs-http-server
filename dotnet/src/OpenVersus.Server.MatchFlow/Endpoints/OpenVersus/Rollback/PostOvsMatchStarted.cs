using FastEndpoints;
using OpenVersus.Server.Core.Matches;

namespace OpenVersus.Server.MatchFlow.Endpoints.OpenVersus.Rollback;

/// <summary>
/// POST /ovs_match_started: a P2P host node's match has begun (<see cref="IRollbackCallbacks.MatchStartedAsync"/>).
/// Seen in: TS server: POST /ovs_match_started; OVSRollbackNode Node (MatchStarted).
/// Server only.
/// </summary>
public sealed class PostOvsMatchStarted : RollbackEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.POST);
        Routes("/ovs_match_started");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        await Resolve<IRollbackCallbacks>().MatchStartedAsync(await ReadBodyAsync(ct));
        await SendEmptyAsync(ct);
    }
}

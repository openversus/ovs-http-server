using FastEndpoints;
using OpenVersus.Server.Core.Matches;

namespace OpenVersus.Server.MatchFlow.Endpoints.OpenVersus.Rollback;

/// <summary>
/// POST /ovs_end_match: every player has left the match's rollback server, or a P2P host's node (<see cref="IRollbackCallbacks.EndMatchAsync"/>).
/// Seen in: TS server: POST /ovs_end_match; ovs-rollback-server HTTPHelper.SendEndMatchAsync.
/// Server only.
/// </summary>
public sealed class PostOvsEndMatch : RollbackEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.POST);
        Routes("/ovs_end_match");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        await Resolve<IRollbackCallbacks>().EndMatchAsync(await ReadBodyAsync(ct), "/ovs_end_match");
        await SendEmptyAsync(ct);
    }
}

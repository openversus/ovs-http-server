using FastEndpoints;
using OpenVersus.Server.Core.Matches;

namespace OpenVersus.Server.MatchFlow.Endpoints.OpenVersus.Rollback;

/// <summary>
/// POST /mvsi_end_match: as /ovs_end_match, from an MVSI rollback server (<see cref="IRollbackCallbacks.EndMatchAsync"/>).
/// Seen in: TS server: POST /mvsi_end_match.
/// Server only.
/// </summary>
public sealed class PostMvsiEndMatch : RollbackEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.POST);
        Routes("/mvsi_end_match");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        await Resolve<IRollbackCallbacks>().EndMatchAsync(await ReadBodyAsync(ct), "/mvsi_end_match");
        await SendEmptyAsync(ct);
    }
}

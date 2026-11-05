using FastEndpoints;
using OpenVersus.Server.Core.Hosting;
using OpenVersus.Server.Core.Matches;

namespace OpenVersus.Server.MatchFlow.Endpoints.OpenVersus.Rollback;

/// <summary>
/// POST /ovs_match_status: a rollback server's match status event, checked by its MatchUpdateKey (<see cref="IMatchStatusEvents"/>).
/// Seen in: TS server: POST /ovs_match_status, /api/ovs_match_status; ovs-rollback-server HTTPHelper.SendMatchStatus.
/// Server only.
/// </summary>
public sealed class PostOvsMatchStatus : RollbackEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.POST);
        Routes("/ovs_match_status");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var (status, answer) = await Resolve<IMatchStatusEvents>().HandleAsync(HttpContext.Request.Headers["MatchUpdateKey"].FirstOrDefault(),
            await ReadBodyAsync(ct), ClientAddress.Of(HttpContext));
        await SendJsonAsync(answer, status, ct);
    }
}

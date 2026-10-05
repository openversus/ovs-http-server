using FastEndpoints;
using OpenVersus.Server.Core.Matches;

namespace OpenVersus.Server.MatchFlow.Endpoints.OpenVersus.Rollback;

/// <summary>
/// GET /ovs_node_config: the settings update every P2P node fetches at startup, signed (<see cref="INodeConfig"/>); 503
/// when there is none to give.
/// Seen in: TS server: GET /ovs_node_config; the rollback repo's NodeLockdown.
/// Server only.
/// </summary>
public sealed class GetOvsNodeConfig : EndpointWithoutRequest
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/ovs_node_config");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (Resolve<INodeConfig>().Answer() is not { } answer)
        {
            await Send.ResultAsync(Microsoft.AspNetCore.Http.Results.StatusCode(503));
            return;
        }

        HttpContext.Response.Headers[INodeConfig.SignatureHeader] = answer.Signature;
        await Send.BytesAsync(answer.Body, contentType: "application/json", cancellation: ct);
    }
}

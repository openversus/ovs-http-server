using System.Text.Json.Nodes;
using FastEndpoints;
using OpenVersus.Server.Core.Matches;
using OpenVersus.Server.Http.Shared.Endpoints;

namespace OpenVersus.Server.MatchFlow.Endpoints.OpenVersus.Rollback;

/// <summary>
/// POST /ovs_match_inputs: a rollback server's recording of its match's inputs (<see cref="IMatchInputs"/>), stored one
/// document per player per match. C# only.
/// Seen in: rollback server (InputRecording).
/// Server only.
/// </summary>
public sealed class PostOvsMatchInputs : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.POST);
        Routes("/ovs_match_inputs");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var (status, answer) = await Resolve<IMatchInputs>().StoreAsync(HttpContext.Request.Headers["MatchUpdateKey"].FirstOrDefault(),
            await ReadBodyAsync(ct) as JsonObject, ct);
        await SendJsonAsync(answer, status, ct);
    }
}

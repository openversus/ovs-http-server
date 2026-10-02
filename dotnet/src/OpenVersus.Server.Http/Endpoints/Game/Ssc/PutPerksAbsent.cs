using FastEndpoints;
using OpenVersus.Server.Core.Perks;
using OpenVersus.Server.Http.Shared.Endpoints;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/perks_absent: the TS server's fixed answer (<see cref="PerksAbsent"/>), as every captured answer was. Follow-up: docs/SSC.md.
/// Seen in: binary ssc name; captured 3x; TS server: PUT /ssc/invoke/perks_absent.
/// </summary>
public sealed class PutPerksAbsent : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/perks_absent");
    }

    public override Task HandleAsync(CancellationToken ct) => SendJsonAsync(PerksAbsent.Answer(), ct);
}

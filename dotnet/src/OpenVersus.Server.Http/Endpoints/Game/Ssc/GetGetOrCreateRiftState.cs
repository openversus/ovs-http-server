using FastEndpoints;
using OpenVersus.Server.Core.Rifts;
using OpenVersus.Server.Http.Hosting;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// GET /ssc/invoke/get_or_create_rift_state: the player's rift state (<see cref="IRiftStateService"/>), which the rift
/// select page waits for.
/// Seen in: binary ssc name; capture (bench, 2026-09-30). The TS server does not answer it.
/// </summary>
public sealed class GetGetOrCreateRiftState : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/ssc/invoke/get_or_create_rift_state");
    }

    public override async Task HandleAsync(CancellationToken ct) =>
        await SendJsonAsync(await Resolve<IRiftStateService>().GetOrCreateAsync(HttpContext.Session()?.Claims, ct), ct);
}

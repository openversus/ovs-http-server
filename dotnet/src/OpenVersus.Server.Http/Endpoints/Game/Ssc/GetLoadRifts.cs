using FastEndpoints;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// GET /ssc/invoke/load_rifts: the rift configurations and one copy of the rifts' runtime data, the same for everyone,
/// as the TS server answers (Static/ssc-load-rifts.json; frozen account data, see RiftHosting). In the login batch.
/// Seen in: binary ssc name; TS server: GET /ssc/invoke/load_rifts.
/// </summary>
public sealed class GetLoadRifts : StaticEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/ssc/invoke/load_rifts");
    }

    public override Task HandleAsync(CancellationToken ct) => SendStaticAsync("ssc-load-rifts", ct);
}

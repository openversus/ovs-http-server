using FastEndpoints;
using OpenVersus.Server.Core.Perks;
using OpenVersus.Server.Http.Shared.Hosting;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// GET /ssc/invoke/perks_get_all_pages: the player's saved perk pages (<see cref="IPerksService"/>). In the login batch.
/// Seen in: binary ssc name; TS server: GET /ssc/invoke/perks_get_all_pages.
/// </summary>
public sealed class GetPerksGetAllPages : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/ssc/invoke/perks_get_all_pages");
    }

    public override async Task HandleAsync(CancellationToken ct) =>
        await SendJsonAsync(await Resolve<IPerksService>().PagesAsync(HttpContext.Session()?.AccountId ?? "", ct), ct);
}

using FastEndpoints;
using OpenVersus.Server.Core.Leaderboards;
using OpenVersus.Server.Http.Hosting;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// GET /ssc/invoke/ranked_data: the ranked screen's season data (<see cref="IRankedDataService"/>). In the login batch.
/// Seen in: binary ssc name; TS server: GET /ssc/invoke/ranked_data.
/// </summary>
public sealed class GetRankedData : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/ssc/invoke/ranked_data");
    }

    public override async Task HandleAsync(CancellationToken ct) =>
        await SendJsonAsync(await Resolve<IRankedDataService>().DataAsync(HttpContext.Session()?.Claims, ct), ct);
}

using FastEndpoints;
using OpenVersus.Server.Core.Seasons;
using OpenVersus.Server.Http.Shared.Endpoints;
using OpenVersus.Server.Http.Shared.Hosting;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// POST /ssc/invoke/attempt_daily_refresh: the current season and the next refresh times (<see cref="ISeasonService"/>).
/// Seen in: binary ssc name; captured 11x; TS server: POST /ssc/invoke/attempt_daily_refresh.
/// </summary>
public sealed class PostAttemptDailyRefresh : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.POST);
        Routes("/ssc/invoke/attempt_daily_refresh");
    }

    public override Task HandleAsync(CancellationToken ct) => SendJsonAsync(Resolve<ISeasonService>().DailyRefresh(), ct);
}

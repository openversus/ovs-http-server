using FastEndpoints;
using OpenVersus.Server.Core.Calendar;
using OpenVersus.Server.Http.Shared.Hosting;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// GET /ssc/invoke/get_calendar_events: the front-end carousel, with the required-update popup for a player who must
/// update (<see cref="ICalendarService"/>). In the login batch.
/// Seen in: binary ssc name; TS server: GET /ssc/invoke/get_calendar_events.
/// </summary>
public sealed class GetGetCalendarEvents : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/ssc/invoke/get_calendar_events");
    }

    public override async Task HandleAsync(CancellationToken ct) =>
        await SendJsonAsync(await Resolve<ICalendarService>().EventsAsync(AccountLookups.From(HttpContext), HttpContext.Session()?.Claims), ct);
}

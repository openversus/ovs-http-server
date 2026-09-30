using FastEndpoints;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// GET /ssc/invoke/get_hiss_calendar_events: the event calendar, the same for everyone, as the TS server answers
/// (Static/ssc-get-hiss-calendar-events.json). Despite the name it has no part in hiss_amalgamation's CRC. In the login
/// batch. Seen in: binary ssc name; TS server: GET /ssc/invoke/get_hiss_calendar_events.
/// </summary>
public sealed class GetGetHissCalendarEvents : StaticEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/ssc/invoke/get_hiss_calendar_events");
    }

    public override Task HandleAsync(CancellationToken ct) => SendStaticAsync("ssc-get-hiss-calendar-events", ct);
}

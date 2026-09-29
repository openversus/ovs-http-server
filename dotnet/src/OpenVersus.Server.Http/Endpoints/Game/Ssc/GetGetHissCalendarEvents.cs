using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// GET /ssc/invoke/get_hiss_calendar_events.
/// Seen in: binary ssc name; TS server: GET /ssc/invoke/get_hiss_calendar_events.
/// Ssc: binary.
/// </summary>
public sealed class GetGetHissCalendarEvents : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/ssc/invoke/get_hiss_calendar_events");
    }
}

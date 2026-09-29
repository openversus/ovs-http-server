using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// GET /ssc/invoke/get_calendar_events.
/// Seen in: binary ssc name; TS server: GET /ssc/invoke/get_calendar_events.
/// Ssc: server/capture.
/// </summary>
public sealed class GetGetCalendarEvents : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/ssc/invoke/get_calendar_events");
    }
}

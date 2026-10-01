using System.Text.Json.Nodes;
using FastEndpoints;
using Microsoft.Extensions.Options;
using OpenVersus.Server.Core.Missions;
using OpenVersus.Server.Core.Static;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// GET /ssc/invoke/get_hiss_calendar_events: the event calendar, the same for everyone, as the TS server answers
/// (Static/ssc-get-hiss-calendar-events.json). Despite the name it has no part in hiss_amalgamation's CRC. In the login
/// batch. With Missions:Enabled, the live mission containers' events run on (<see cref="MissionCalendar"/>).
/// Seen in: binary ssc name; TS server: GET /ssc/invoke/get_hiss_calendar_events.
/// </summary>
public sealed class GetGetHissCalendarEvents : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/ssc/invoke/get_hiss_calendar_events");
    }

    public override Task HandleAsync(CancellationToken ct)
    {
        var calendar = JsonNode.Parse(StaticResponses.Json("ssc-get-hiss-calendar-events"))!;
        return MissionCalendar.Adjust(calendar, Resolve<IOptionsMonitor<MissionSettings>>().CurrentValue)
            ? SendJsonAsync(calendar, ct)
            : SendStaticAsync("ssc-get-hiss-calendar-events", ct);
    }
}

using System.ComponentModel;
using System.Text.Json.Nodes;

namespace OpenVersus.Server.Core.Matchmaking;

// The public Free For All queue's hours, ported from the TS server's services/ffaSchedule.ts. With Ffa:WeekendOnly on
// (FFA_WEEKEND_ONLY, on by default as there) the queue is open from Friday 00:00 to Sunday 23:59 US Eastern, EST or EDT
// as the date has it; Sunday's last minute is already closed (as there). Read by the FFA request
// (MatchmakingRequestService: refused outside the window with ClosedFailure) and by the matchmaker (MatchmakingWorker:
// outside the window it cancels every queued FFA ticket). The TS website (server.ts) reads its own FFA_WEEKEND_ONLY for
// the FFA searching count: keep the two the same.

/// <summary>The public FFA queue's settings.</summary>
public sealed class FfaSettings
{
    [Description("The public FFA queue is open only from Friday 00:00 to Sunday 23:59 US Eastern (FFA_WEEKEND_ONLY). Outside it an FFA request is refused and the matchmaker cancels every queued FFA ticket. Off: always open. The TS website reads its own FFA_WEEKEND_ONLY: keep the two the same.")]
    public bool WeekendOnly { get; set; } = true;
}

public static class FfaSchedule
{
    public const string ScheduleText = "Free For All is open Friday 12:00am to Sunday 11:59pm Eastern.";
    private const int SundayCloseMinute = 23 * 60 + 59;

    // The IANA name, as the TS server's Intl; Windows with invariant globalization cannot map IANA names, so its own
    // name there.
    private static readonly TimeZoneInfo s_eastern = TimeZoneInfo.TryFindSystemTimeZoneById("America/New_York", out var eastern)
        ? eastern
        : TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

    /// <summary>Whether the FFA queue takes players at <paramref name="now"/> (isFfaQueueOpen).</summary>
    public static bool IsOpen(DateTimeOffset now, bool weekendOnly)
    {
        if (!weekendOnly)
        {
            return true;
        }

        var local = TimeZoneInfo.ConvertTime(now, s_eastern);
        return local.DayOfWeek switch
        {
            DayOfWeek.Friday or DayOfWeek.Saturday => true,
            DayOfWeek.Sunday => local.Hour * 60 + local.Minute < SundayCloseMinute,
            _ => false,
        };
    }

    /// <summary>The answer to an FFA request outside the window (ffaQueueClosedFailure): a Hydra action failure, HTTP 200.</summary>
    public static JsonObject ClosedFailure() => new()
    {
        ["body"] = new JsonObject
        {
            ["error"] = "ffa_queue_closed",
            ["ErrorCode"] = "QueueClosed",
            ["ErrorMessage"] = ScheduleText,
        },
        ["metadata"] = null,
        ["return_code"] = 1,
    };
}

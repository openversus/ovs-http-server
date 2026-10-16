using System.ComponentModel;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;

namespace OpenVersus.Server.Core.Matchmaking;

// The public Free For All queue's hours, ported from the TS server's services/ffaSchedule.ts. With Ffa:WeekendOnly on
// (FFA_WEEKEND_ONLY, on by default) the queue is open on Ffa:Days, each the whole day US Eastern, EST or EDT as the date
// has it (2026-10-16, Tuggernuts: FFA and Testing Grounds swap by the day, Testing Grounds the more days; until then
// Friday 00:00 to Sunday 23:59). On an FFA day Twosday closes it for its hours, as it closes Testing Grounds (neither tile
// shows then; Tuggernuts, 2026-10-16). Testing Grounds is open on the other days (TestingGrounds.IsOpen). Read by the FFA request
// (MatchmakingRequestService: refused outside the window with ClosedFailure) and by the matchmaker (MatchmakingWorker:
// outside the window it cancels every queued FFA ticket). The TS website (server.ts) reads its own FFA_WEEKEND_ONLY for
// the FFA searching count: keep the two the same.
// Its matches run Ffa:Mutators (Tuggernuts, 2026-10-14: last man standing, as always, plus Beta Speed's 10%): the
// matchmaker puts them in the match's worldBuffs (the rollback server's tick rate and every client's speed read them),
// and with Beta Speed a client that runs another speed is refused as from Testing Grounds (WrongBetaSpeed).

/// <summary>The public FFA queue's settings.</summary>
public sealed class FfaSettings
{
    [Description("On: the public FFA queue is open only on Ffa:Days, US Eastern (FFA_WEEKEND_ONLY, the name it had while those were the weekend). Outside them an FFA request is refused and the matchmaker cancels every queued FFA ticket; Testing Grounds takes those days. Off: always open.")]
    public bool WeekendOnly { get; set; } = true;

    [Description("The days the FFA queue is open while Ffa:WeekendOnly is on, each 00:00 to 24:00 US Eastern, comma-separated (Mon, Tuesday, ...; any case). Testing Grounds has the others. Default Tue,Thu,Sat. None that parses: FFA is never open.")]
    public string Days { get; set; } = "Tue,Thu,Sat";

    [Description("The OVS mutators every public FFA match runs, comma-separated slugs (its world buffs). Default ovs_beta_speed: the whole match at BetaSpeed:Percent; a client that runs another speed is refused from the queue until it restarts. Empty: none.")]
    public string Mutators { get; set; } = "ovs_beta_speed";
}

public static class FfaSchedule
{
    /// <summary>The mutators of its matches, in order, without blanks or repeats.</summary>
    public static string[] MutatorsOf(FfaSettings settings) =>
        settings.Mutators.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToArray();

    /// <summary>Its match's worldBuffs: the mutators (the rollback registry's tick_rate and the clients' speed read them).</summary>
    public static JsonArray WorldBuffs(FfaSettings settings) => new(MutatorsOf(settings).Select(m => (JsonNode?)m).ToArray());

    /// <summary>
    /// Whether the mutators include Beta Speed and <paramref name="header"/> (the client's X-OVS-BetaSpeed) does not report
    /// <paramref name="betaSpeed"/>'s speed: missing (an older client), or read before a change.
    /// </summary>
    public static bool WrongBetaSpeed(FfaSettings settings, Matches.BetaSpeedSettings betaSpeed, string? header) =>
        MutatorsOf(settings).Contains(Matches.GameplayConfigs.BetaSpeedMutator) && !Matches.BetaSpeed.Reports(header, betaSpeed.Percent);

    /// <summary>The days in <paramref name="text"/> (Ffa:Days), Monday first: full names or their first three letters, any case; others ignored.</summary>
    public static DayOfWeek[] DaysOf(string? text)
    {
        var days = new HashSet<DayOfWeek>();
        foreach (string part in (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var day in Enum.GetValues<DayOfWeek>())
            {
                string name = day.ToString();
                if (part.Equals(name, StringComparison.OrdinalIgnoreCase) || (part.Length == 3 && name.StartsWith(part, StringComparison.OrdinalIgnoreCase)))
                {
                    days.Add(day);
                }
            }
        }

        return days.OrderBy(d => ((int)d + 6) % 7).ToArray();
    }

    /// <summary>"Tuesday, Thursday and Saturday": the days as a sentence names them.</summary>
    public static string DayList(IEnumerable<DayOfWeek> days)
    {
        var names = days.Select(d => d.ToString()).ToList();
        return names.Count switch
        {
            0 => "no day",
            1 => names[0],
            _ => string.Join(", ", names[..^1]) + " and " + names[^1],
        };
    }

    /// <summary>What a player turned away is told.</summary>
    public static string ScheduleText(FfaSettings settings) => $"Free For All is open {DayList(DaysOf(settings.Days))}, US Eastern, except during Twosday.";

    // The IANA name, as the TS server's Intl; Windows with invariant globalization cannot map IANA names, so its own
    // name there.
    private static readonly TimeZoneInfo s_eastern = TimeZoneInfo.TryFindSystemTimeZoneById("America/New_York", out var eastern)
        ? eastern
        : TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

    /// <summary>
    /// Whether the FFA queue takes players at <paramref name="now"/> (isFfaQueueOpen): always with Ffa:WeekendOnly off, else
    /// on Ffa:Days, US Eastern, outside <paramref name="twosday"/>.
    /// </summary>
    public static bool IsOpen(DateTimeOffset now, FfaSettings settings, bool twosday = false) =>
        !settings.WeekendOnly || (DaysOf(settings.Days).Contains(TimeZoneInfo.ConvertTime(now, s_eastern).DayOfWeek) && !twosday);

    /// <summary><see cref="IsOpen"/> with the service's settings and Twosday's switch (Redis), read only on an FFA day.</summary>
    public static Task<bool> IsOpenAsync(IServiceProvider services, DateTimeOffset now) =>
        IsOpenAsync(services, now, services.GetService<Microsoft.Extensions.Options.IOptionsMonitor<FfaSettings>>()?.CurrentValue ?? new FfaSettings());

    /// <summary><see cref="IsOpen"/> with <paramref name="settings"/> and Twosday's switch (Redis), read only on an FFA day.</summary>
    public static async Task<bool> IsOpenAsync(IServiceProvider services, DateTimeOffset now, FfaSettings settings)
    {
        if (!IsOpen(now, settings) || !settings.WeekendOnly)
        {
            return IsOpen(now, settings);
        }

        return IsOpen(now, settings, await Twosday.IsActiveAsync(services, now));
    }

    /// <summary>The answer to an FFA request outside the window (ffaQueueClosedFailure): a Hydra action failure, HTTP 200.</summary>
    public static JsonObject ClosedFailure(FfaSettings settings) => new()
    {
        ["body"] = new JsonObject
        {
            ["error"] = "ffa_queue_closed",
            ["ErrorCode"] = "QueueClosed",
            ["ErrorMessage"] = ScheduleText(settings),
        },
        ["metadata"] = null,
        ["return_code"] = 1,
    };
}

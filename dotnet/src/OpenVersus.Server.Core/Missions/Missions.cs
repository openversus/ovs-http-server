using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using OpenVersus.Server.Core.Settings;

namespace OpenVersus.Server.Core.Missions;

// POST /ssc/invoke/get_or_create_mission_object. With Missions:Enabled, the player's own missions (MissionService). Off,
// the TS server's answer (handleSsc_invoke_get_or_create_mission_object and applyMissionsSwitch, handlers/ssc.ts, with
// MISSIONS_ENABLED off as on prod): its fixed object (mission-object.json, generated from the TS source by
// tools/missions/gen_mission_object.mjs) with owner_id the player's id and no containers.
//
// The fixed object is one WB account's, as it stood when copied (its id 6658a026... is the one in WB's own websocket
// messages): its progress, GUIDs and dates are that account's. Its containers are never sent; it is kept as the
// reference for the shape (docs/MISSIONS.md).
//
// Unlike there: a request with no player id gets no owner_id (JSON.stringify drops the TS server's undefined; its
// Hydra encoder would write NaN).

/// <summary>Mission settings.</summary>
public sealed class MissionSettings
{
    [Description("Missions work: each player gets their own missions, rolled from the game data for the containers in Missions:Containers and refreshed at the resets below. Off: no missions, as the TS server answers (MISSIONS_ENABLED off).")]
    public bool Enabled { get; set; }

    [Description("The mission containers players get (slugs from the hiss mission-containers; a trailing * matches a prefix), separated by commas.")]
    public string Containers { get; set; } = "miscon_battlepassdaily_s5, miscon_battlepassweekly_s5, miscon_unlockable_*";

    [Description("The hour (UTC, 0-23) of the daily reset: Daily containers get new missions after it, and attempt_daily_refresh tells the game when the next one is.")]
    [Range(0, 23)]
    public int ResetHourUtc { get; set; } = 11;

    [Description("The minute (0-59) of the daily reset's hour.")]
    [Range(0, 59)]
    public int ResetMinute { get; set; }

    [Description("The day of the weekly reset (at the daily reset's time): Weekly containers get new missions after it.")]
    public DayOfWeek WeeklyResetDay { get; set; } = DayOfWeek.Tuesday;

    [Description("Custom games move missions too (off: only matchmade and rift matches do).")]
    public bool CustomGamesProgress { get; set; }

    [Description("How many years later the events of the live containers (their AssociatedEvent, e.g. evt_battlepass_season_five) end, in the calendar the game is sent (get_hiss_calendar_events), so their missions and battle pass show as running. 0: as the calendar has them. Only while Missions:Enabled.")]
    [Range(0, 100)]
    public int EventEndYears { get; set; } = 20;
}

/// <summary>The daily and weekly resets (Missions:ResetHourUtc, ResetMinute, WeeklyResetDay).</summary>
public static class MissionClock
{
    /// <summary>The latest daily reset at or before <paramref name="now"/>.</summary>
    public static DateTimeOffset LastDaily(MissionSettings settings, DateTimeOffset now)
    {
        var utc = now.ToUniversalTime();
        var today = new DateTimeOffset(utc.Year, utc.Month, utc.Day, settings.ResetHourUtc, settings.ResetMinute, 0, TimeSpan.Zero);
        return today <= utc ? today : today.AddDays(-1);
    }

    /// <summary>The latest weekly reset at or before <paramref name="now"/>.</summary>
    public static DateTimeOffset LastWeekly(MissionSettings settings, DateTimeOffset now)
    {
        var daily = LastDaily(settings, now);
        return daily.AddDays(-(((int)daily.DayOfWeek - (int)settings.WeeklyResetDay + 7) % 7));
    }

    /// <summary>The next daily reset after <paramref name="now"/>.</summary>
    public static DateTimeOffset NextDaily(MissionSettings settings, DateTimeOffset now) => LastDaily(settings, now).AddDays(1);

    /// <summary>The next weekly reset after <paramref name="now"/>.</summary>
    public static DateTimeOffset NextWeekly(MissionSettings settings, DateTimeOffset now) => LastWeekly(settings, now).AddDays(7);
}

public static class MissionObject
{
    private static readonly Lazy<JsonObject> s_object = new(() =>
    {
        using var stream = typeof(MissionObject).Assembly.GetManifestResourceStream("OpenVersus.Server.Core.Missions.mission-object.json")
            ?? throw new InvalidOperationException("mission-object.json is not embedded");
        return JsonNode.Parse(stream)!.AsObject();
    });

    /// <summary>The get_or_create_mission_object answer for <paramref name="accountId"/> (a copy).</summary>
    public static JsonObject Answer(string? accountId, bool enabled)
    {
        var answer = s_object.Value.DeepClone().AsObject();
        var body = answer["body"]!.AsObject();
        if (string.IsNullOrEmpty(accountId))
        {
            body.Remove("owner_id");
        }
        else
        {
            body["owner_id"] = accountId;
        }

        if (!enabled)
        {
            body["server_data"]!["MissionControllerContainers"] = new JsonObject();
        }

        return answer;
    }
}

public static class MissionHosting
{
    public static WebApplicationBuilder AddMissions(this WebApplicationBuilder builder)
    {
        builder.AddSetting<MissionSettings>("Missions");
        builder.Services.AddSingleton<MissionRandom>();
        builder.Services.AddSingleton<IMissionService, MissionService>();
        builder.Services.AddHostedService<MissionResultSubscriber>();
        return builder;
    }
}

/// <summary>
/// The calendar the game is sent (get_hiss_calendar_events) with the live containers' events running: with
/// Missions:Enabled and Missions:EventEndYears, each event a live container names (AssociatedEvent) ends that many years
/// later (data.TimeSpan.EndTime's year, entry_options.end_at) and is marked active. The server rolls those containers'
/// missions; the calendar is what makes the game show them (and the event's battle pass) as running, so the two agree.
/// </summary>
public static class MissionCalendar
{
    /// <summary>Moves the events in <paramref name="calendar"/> (a parsed answer); true when any changed.</summary>
    public static bool Adjust(JsonNode calendar, MissionSettings settings)
    {
        if (!settings.Enabled || settings.EventEndYears == 0)
        {
            return false;
        }

        var events = MissionService.LiveContainers(settings.Containers)
            .Select(slug => Hiss.HissTables.Data("mission-containers", slug)?["MvsMissionControllerContainerData"]?["AssociatedEvent"]?.GetValue<string>())
            .Where(e => !string.IsNullOrEmpty(e))
            .ToHashSet();
        bool changed = false;
        foreach (var entry in Walk(calendar).Where(o => o["name"] is JsonValue n && n.TryGetValue(out string? name) && events.Contains(name)))
        {
            if (entry["data"]?["TimeSpan"]?["EndTime"] is JsonObject end && end["Year"] is JsonValue year && year.TryGetValue(out int y))
            {
                end["Year"] = y + settings.EventEndYears;
            }

            if (entry["entry_options"]?["end_at"] is JsonObject endAt && endAt["_hydra_unix_date"] is JsonValue at && at.TryGetValue(out long seconds))
            {
                endAt["_hydra_unix_date"] = DateTimeOffset.FromUnixTimeSeconds(seconds).AddYears(settings.EventEndYears).ToUnixTimeSeconds();
            }

            if (entry.ContainsKey("bIsCurrentlyActive"))
            {
                entry["bIsCurrentlyActive"] = true;
            }

            changed = true;
        }

        return changed;
    }

    private static IEnumerable<JsonObject> Walk(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                yield return o;
                foreach (var (_, child) in o)
                {
                    foreach (var inner in Walk(child))
                    {
                        yield return inner;
                    }
                }

                break;
            case JsonArray a:
                foreach (var child in a)
                {
                    foreach (var inner in Walk(child))
                    {
                        yield return inner;
                    }
                }

                break;
        }
    }
}

public static class MissionContainers
{
    /// <summary>The live mission containers of a Missions:Containers value (those the hiss has and enables).</summary>
    public static IReadOnlyList<string> Live(string setting) => MissionService.LiveContainers(setting);
}

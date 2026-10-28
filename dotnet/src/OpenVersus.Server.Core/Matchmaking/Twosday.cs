using System.ComponentModel;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Matchmaking;

// Twosday, ported from the TS server (src/services/twosdayService.ts, twosdayPartyBreakup.ts and src/utils/twosdayWindow.ts,
// branch openversus). While it is on and inside its window (Tuesdays 15:00 to 24:00 America/New_York by default) everyone
// solo queues 2v2 and gets a random partner:
//   - 1v1 is off (Tuggernuts, 2026-10-27; the TS server sent a 1v1 search to the 2v2 queue instead, and players did not
//     know why their 1v1 was a 2v2): a party lobby set to 1v1 is kept on 2v2 and the game told so (PartyService.SetModeAsync,
//     OnLobbyModeUpdated), and a 1v1 search that comes anyway is refused with OneVsOneOffFailure (MatchmakingRequestService);
//     the catalog has no switch for the 1v1 tile itself, as it has for the event queues;
//   - no party lobby can be formed: a party invite is answered as sent and never delivered (PartyService.InviteAsync),
//     and a join into someone else's party lobby is refused (PartyService.JoinAsync, PartyLobbyService); a party formed
//     before the window keeps working and can still queue together;
//   - for its first `breakup_minutes` (default 30) every party lobby is broken up as soon as none of its players is
//     queued, in a match or in a ranked set (TwosdayPartyBreakup, every 15 s);
//   - Testing Grounds and FFA are closed (TestingGrounds, FfaSchedule).
// Custom lobbies and Arena lobbies, their invites and their joins, are untouched.
// Twosday:Enabled (TWOSDAY_ENABLED, on by default) is the deployment's switch: off, Twosday never happens there whatever
// the hash says (a test server). Then, switched hot through the Redis hash `twosday`, read on every check (no restart):
//   HSET twosday enabled 0        off (also false / off); 1 or no field: on
//   HSET twosday day 2            0 Sunday .. 6 Saturday (default 2, Tuesday)
//   HSET twosday start 15:00      HH:MM or HH:MM:SS, inclusive (default 15:00)
//   HSET twosday end 24:00        HH:MM or HH:MM:SS, exclusive, 24:00 = midnight (default 24:00)
//   HSET twosday tz America/New_York   the clock the hours are on (default America/New_York, daylight saving included)
//   HSET twosday breakup_minutes 30    how long after the window opens the party lobbies are broken up (0: never)
// To try it on a local server: HSET twosday day <today, 0 = Sunday> start <a minute ago> end 24:00.
// A value that cannot be read (a bad time, day or zone) or Redis failing counts as off. The TS server also wrote
// `enabled 1` at startup when the field was missing; a missing field already reads as on, so nothing is written here.

/// <summary>Twosday's deployment switch.</summary>
public sealed class TwosdaySettings
{
    [Description("Twosday on this deployment (TWOSDAY_ENABLED): its window (the Redis hash twosday: enabled, day, start, end, tz, breakup_minutes) sends every ranked 1v1 search to 2v2, stops new parties, breaks up existing ones and closes Testing Grounds and FFA. Off: never Twosday here, whatever the hash says (a test server).")]
    public bool Enabled { get; set; } = true;
}

public static class Twosday
{
    public const string Key = "twosday";

    /// <summary>Twosday's weekly window: one day, start (inclusive) to end (exclusive) on that zone's wall clock.</summary>
    public sealed record Window(DayOfWeek Day, string Start, string End, string TimeZone);

    /// <summary>Tuesdays from 3 PM to midnight, US Eastern time.</summary>
    public static readonly Window Default = new(DayOfWeek.Tuesday, "15:00", "24:00", "America/New_York");

    /// <summary>The minutes into the window during which party lobbies are broken up, when the hash does not say.</summary>
    public const int DefaultBreakupMinutes = 30;

    /// <summary>What both players of a party that is broken up are told.</summary>
    public const string PartiesOffMessage = "Parties are off for Twosday: queue solo for a random partner";

    /// <summary>The answer to a 1v1 search during Twosday: a Hydra action failure, HTTP 200, as a closed queue's.</summary>
    public static System.Text.Json.Nodes.JsonObject OneVsOneOffFailure() => new()
    {
        ["body"] = new System.Text.Json.Nodes.JsonObject
        {
            ["error"] = "twosday_one_vs_one_off",
            ["ErrorCode"] = "QueueClosed",
            ["ErrorMessage"] = "It's Twosday: 1v1 is off until midnight (US Eastern). Queue 2v2 for a random partner.",
        },
        ["metadata"] = null,
        ["return_code"] = 1,
    };

    /// <summary>A lobby mode that is 1v1 (as the game's mode select and the TS server have named it), which Twosday turns into its 2v2.</summary>
    public static bool IsOneVsOneMode(string mode) => mode is "1v1" or "ranked-1v1" or "1v1_ranked";

    /// <summary>Seconds since midnight for "HH:MM" or "HH:MM:SS" (24:00 allowed); null when it is not a time.</summary>
    public static int? SecondsOfDay(string text)
    {
        string[] parts = text.Trim().Split(':');
        if (parts.Length is < 2 or > 3 || parts[0].Length is < 1 or > 2 || parts.Skip(1).Any(p => p.Length != 2)
            || !parts.All(p => p.All(char.IsAsciiDigit)))
        {
            return null;
        }

        int h = int.Parse(parts[0], CultureInfo.InvariantCulture), m = int.Parse(parts[1], CultureInfo.InvariantCulture);
        int s = parts.Length == 3 ? int.Parse(parts[2], CultureInfo.InvariantCulture) : 0;
        return m > 59 || s > 59 || h > 24 || (h == 24 && (m > 0 || s > 0)) ? null : h * 3600 + m * 60 + s;
    }

    /// <summary>Whether <paramref name="now"/> is inside <paramref name="window"/>. Throws on a window it cannot read.</summary>
    public static bool IsInWindow(DateTimeOffset now, Window window) => SecondsInto(now, window) is not null;

    /// <summary>
    /// How long <paramref name="now"/> is into <paramref name="window"/>, in seconds; null outside it (secondsIntoTwosdayWindow).
    /// Throws on a window it cannot read.
    /// </summary>
    public static int? SecondsInto(DateTimeOffset now, Window window)
    {
        int start = SecondsOfDay(window.Start) ?? throw new FormatException($"bad Twosday start: {window.Start}");
        int end = SecondsOfDay(window.End) ?? throw new FormatException($"bad Twosday end: {window.End}");
        if (start >= end)
        {
            throw new FormatException($"bad Twosday hours: {window.Start} to {window.End}");
        }

        var local = TimeZoneInfo.ConvertTime(now, Zone(window.TimeZone));
        int seconds = (int)local.TimeOfDay.TotalSeconds;
        return local.DayOfWeek == window.Day && seconds >= start && seconds < end ? seconds - start : null;
    }

    /// <summary>Whether the deployment allows Twosday at all (Twosday:Enabled).</summary>
    public static bool IsEnabled(IServiceProvider services) =>
        (services.GetService<IOptionsMonitor<TwosdaySettings>>()?.CurrentValue ?? new TwosdaySettings()).Enabled;

    /// <summary>
    /// Whether Twosday is on at <paramref name="now"/>: the deployment allows it (Twosday:Enabled), the hash's switch is on
    /// (a missing field is on) and it is inside its window. Off without Redis, when Redis fails, or when a value cannot be read.
    /// </summary>
    public static async Task<bool> IsActiveAsync(IServiceProvider services, DateTimeOffset now)
    {
        if (!IsEnabled(services) || services.GetService<IConnectionMultiplexer>()?.GetDatabase() is not { } redis)
        {
            return false;
        }

        try
        {
            var fields = (await redis.HashGetAllAsync(Key)).ToDictionary(e => e.Name.ToString(), e => e.Value.ToString());
            return IsActive(fields, now);
        }
        catch (Exception e) when (e is RedisException or FormatException or TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }

    /// <summary>The switch and window from the hash's fields (defaults filled in). Throws on a value it cannot read.</summary>
    public static bool IsActive(IReadOnlyDictionary<string, string> fields, DateTimeOffset now)
    {
        var settings = SettingsOf(fields);
        return settings.Enabled && IsInWindow(now, settings.Window);
    }

    /// <summary>The hash as stored: the switch, the window and the breakup minutes, defaults filled in.</summary>
    public sealed record Settings(bool Enabled, Window Window, int BreakupMinutes);

    /// <summary>
    /// The settings from the hash's fields (readTwosdaySettings). Throws on a day or breakup_minutes it cannot read (a bad
    /// time or zone throws when the window is read).
    /// </summary>
    public static Settings SettingsOf(IReadOnlyDictionary<string, string> fields)
    {
        string Get(string name) => fields.TryGetValue(name, out var v) ? v : "";
        bool enabled = Get("enabled").Trim().ToLowerInvariant() is not ("0" or "false" or "off");
        string day = Get("day");
        var window = new Window(
            day == "" ? Default.Day : int.TryParse(day, CultureInfo.InvariantCulture, out int d) && d is >= 0 and <= 6 ? (DayOfWeek)d
                : throw new FormatException($"bad Twosday day: {day}"),
            Get("start") is { Length: > 0 } start ? start : Default.Start,
            Get("end") is { Length: > 0 } end ? end : Default.End,
            Get("tz") is { Length: > 0 } tz ? tz : Default.TimeZone);
        string breakup = Get("breakup_minutes");
        int breakupMinutes = breakup == "" ? DefaultBreakupMinutes
            : int.TryParse(breakup, NumberStyles.None, CultureInfo.InvariantCulture, out int minutes) ? minutes
            : throw new FormatException($"bad Twosday breakup_minutes: {breakup}");
        return new Settings(enabled, window, breakupMinutes);
    }

    /// <summary>The settings in Redis (<see cref="SettingsOf"/>). Throws when Redis fails or a value cannot be read.</summary>
    public static async Task<Settings> ReadAsync(IDatabase redis) =>
        SettingsOf((await redis.HashGetAllAsync(Key)).ToDictionary(e => e.Name.ToString(), e => e.Value.ToString()));

    // The IANA zone; on Windows with invariant globalization IANA names do not resolve, so through its Windows id.
    private static TimeZoneInfo Zone(string ianaId)
    {
        if (TimeZoneInfo.TryFindSystemTimeZoneById(ianaId, out var zone))
        {
            return zone;
        }

        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(ianaId, out string? windowsId) && TimeZoneInfo.TryFindSystemTimeZoneById(windowsId, out zone))
        {
            return zone;
        }

        return ianaId == "America/New_York" ? TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time") : throw new TimeZoneNotFoundException(ianaId);
    }
}

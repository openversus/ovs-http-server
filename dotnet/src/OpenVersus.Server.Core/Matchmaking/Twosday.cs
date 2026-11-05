using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace OpenVersus.Server.Core.Matchmaking;

// Twosday, ported from the TS server (src/services/twosdayService.ts, twosdayPartyBreakup.ts and src/utils/twosdayWindow.ts,
// branch openversus). While it is on and inside its window (Tuesdays 15:00 to 24:00 America/New_York by default) everyone
// solo queues 2v2 and gets a random partner:
//   - every 1v1 search goes to its 2v2 queue, as the TS server sent it (restored 2026-11-02; from 10-27 to then the port
//     refused 1v1 instead): ranked 1v1 to the ranked 2v2 queue, with the 2v2 rating as its skill, and 1v1 Testing Grounds to
//     2v2 Testing Grounds (MatchmakingRequestService.OneVersusOneAsync); the answer stays the 1v1 request's, the ticket is a
//     2v2 one. A party lobby set to 1v1 is kept on 2v2 and the game told so (PartyService.SetModeAsync, OnLobbyModeUpdated;
//     not in the TS server), so the lobby shows what the search will be;
//   - no party lobby can be formed: a party invite is answered as sent and never delivered (PartyService.InviteAsync),
//     and a join into someone else's party lobby is refused (PartyService.JoinAsync, PartyLobbyService); a party formed
//     before the window keeps working and can still queue together;
//   - for its first Twosday:BreakupMinutes (default 30) every party lobby is broken up as soon as none of its players is
//     queued, in a match or in a ranked set (TwosdayPartyBreakup, every 15 s);
//   - FFA is closed, and 2v2 Testing Grounds takes its slot (TestingGrounds, FfaSchedule).
// Custom lobbies and Arena lobbies, their invites and their joins, are untouched.
// The switch and the window are settings (2026-11-02; the TS server kept them in the Redis hash `twosday`, which is not read
// any more: an HSET on it does nothing), changed while running with ovsctl, cluster-wide:
//   ovsctl settings set Twosday:Enabled false          off (a test server; or Twosday called off)
//   ovsctl settings set Twosday:Day Wednesday          the day (Sunday .. Saturday; default Tuesday)
//   ovsctl settings set Twosday:Start 15:00            HH:MM or HH:MM:SS, inclusive (default 15:00)
//   ovsctl settings set Twosday:End 24:00              HH:MM or HH:MM:SS, exclusive, 24:00 = midnight (default 24:00)
//   ovsctl settings set Twosday:TimeZone America/New_York   the clock the hours are on (daylight saving included)
//   ovsctl settings set Twosday:BreakupMinutes 30      how long after the window opens party lobbies are broken up (0: never)
// To try it on a local server: Day <today>, Start <a minute ago>, End 24:00. A window that cannot be read (a bad time, a
// start at or after the end, an unknown zone) counts as off.

/// <summary>Twosday's switch and window.</summary>
public sealed class TwosdaySettings
{
    [Description("Twosday on this deployment: in its window (Twosday:Day, Twosday:Start to Twosday:End on Twosday:TimeZone's clock) every 1v1 search goes to its 2v2 queue (ranked and Testing Grounds), no party can form, party lobbies are broken up for Twosday:BreakupMinutes, FFA closes and 2v2 Testing Grounds takes its slot. Off: never Twosday here (a test server).")]
    public bool Enabled { get; set; } = true;

    [Description("The day of the week Twosday is on.")]
    public DayOfWeek Day { get; set; } = DayOfWeek.Tuesday;

    [Description("When the window opens, HH:MM or HH:MM:SS on Twosday:TimeZone's clock, inclusive; must be before Twosday:End.")]
    [RegularExpression(@"^\d{1,2}:\d{2}(:\d{2})?$")]
    public string Start { get; set; } = "15:00";

    [Description("When the window closes, HH:MM or HH:MM:SS on Twosday:TimeZone's clock, exclusive; 24:00 is midnight.")]
    [RegularExpression(@"^\d{1,2}:\d{2}(:\d{2})?$")]
    public string End { get; set; } = "24:00";

    [Description("The IANA time zone the hours are on (daylight saving included), e.g. America/New_York.")]
    public string TimeZone { get; set; } = "America/New_York";

    [Description("How long after the window opens the party lobbies are broken up, in minutes (every party lobby is broken up as soon as none of its players is queued, in a match or in a ranked set); 0: never.")]
    [Range(0, 1440)]
    public int BreakupMinutes { get; set; } = 30;
}

public static class Twosday
{
    /// <summary>Twosday's weekly window: one day, start (inclusive) to end (exclusive) on that zone's wall clock.</summary>
    public sealed record Window(DayOfWeek Day, string Start, string End, string TimeZone);

    /// <summary>Tuesdays from 3 PM to midnight, US Eastern time: the settings' defaults.</summary>
    public static readonly Window Default = WindowOf(new TwosdaySettings());

    /// <summary>What both players of a party that is broken up are told.</summary>
    public const string PartiesOffMessage = "Parties are off for Twosday: queue solo for a random partner";

    /// <summary>The window <paramref name="settings"/> describe.</summary>
    public static Window WindowOf(TwosdaySettings settings) => new(settings.Day, settings.Start, settings.End, settings.TimeZone);

    /// <summary>The settings in force (defaults when the service has none).</summary>
    public static TwosdaySettings Of(IServiceProvider services) =>
        services.GetService<IOptionsMonitor<TwosdaySettings>>()?.CurrentValue ?? new TwosdaySettings();

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
    public static bool IsEnabled(IServiceProvider services) => Of(services).Enabled;

    /// <summary>
    /// Whether Twosday is on at <paramref name="now"/>: the switch is on and it is inside the window (the Twosday settings).
    /// Off when the window cannot be read.
    /// </summary>
    public static bool IsActive(IServiceProvider services, DateTimeOffset now) => IsActive(Of(services), now);

    /// <summary>Whether <paramref name="settings"/> put Twosday on at <paramref name="now"/>; off when the window cannot be read.</summary>
    public static bool IsActive(TwosdaySettings settings, DateTimeOffset now)
    {
        try
        {
            return settings.Enabled && IsInWindow(now, WindowOf(settings));
        }
        catch (Exception e) when (e is FormatException or TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }

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

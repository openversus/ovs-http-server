using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Matchmaking;

// Twosday's window, read as the TS server reads it (src/services/twosdayService.ts and src/utils/twosdayWindow.ts,
// branch openversus): the Redis hash `twosday` (enabled, day, start, end, tz), Tuesdays 15:00 to 24:00
// America/New_York by default, on unless `enabled` says 0/false/off, and off when a value cannot be read or Redis fails.
// Only the window is read here: Twosday itself (every 1v1 and 2v2 queue to 2v2, no parties) is the TS server's until
// it is ported. 1v1 Testing Grounds closes while it is on (TestingGrounds).

public static class Twosday
{
    public const string Key = "twosday";

    /// <summary>Twosday's weekly window: one day, start (inclusive) to end (exclusive) on that zone's wall clock.</summary>
    public sealed record Window(DayOfWeek Day, string Start, string End, string TimeZone);

    /// <summary>Tuesdays from 3 PM to midnight, US Eastern time.</summary>
    public static readonly Window Default = new(DayOfWeek.Tuesday, "15:00", "24:00", "America/New_York");

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
    public static bool IsInWindow(DateTimeOffset now, Window window)
    {
        int start = SecondsOfDay(window.Start) ?? throw new FormatException($"bad Twosday start: {window.Start}");
        int end = SecondsOfDay(window.End) ?? throw new FormatException($"bad Twosday end: {window.End}");
        if (start >= end)
        {
            throw new FormatException($"bad Twosday hours: {window.Start} to {window.End}");
        }

        var local = TimeZoneInfo.ConvertTime(now, Zone(window.TimeZone));
        int seconds = (int)local.TimeOfDay.TotalSeconds;
        return local.DayOfWeek == window.Day && seconds >= start && seconds < end;
    }

    /// <summary>
    /// Whether Twosday is on at <paramref name="now"/>: the switch on (a missing field is on) and inside its window.
    /// Off without Redis, when Redis fails, or when a value cannot be read.
    /// </summary>
    public static async Task<bool> IsActiveAsync(IServiceProvider services, DateTimeOffset now)
    {
        if (services.GetService<IConnectionMultiplexer>()?.GetDatabase() is not { } redis)
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
        string Get(string name) => fields.TryGetValue(name, out var v) ? v : "";
        if (Get("enabled").Trim().ToLowerInvariant() is "0" or "false" or "off")
        {
            return false;
        }

        string day = Get("day");
        var window = new Window(
            day == "" ? Default.Day : int.TryParse(day, CultureInfo.InvariantCulture, out int d) && d is >= 0 and <= 6 ? (DayOfWeek)d
                : throw new FormatException($"bad Twosday day: {day}"),
            Get("start") is { Length: > 0 } start ? start : Default.Start,
            Get("end") is { Length: > 0 } end ? end : Default.End,
            Get("tz") is { Length: > 0 } tz ? tz : Default.TimeZone);
        return IsInWindow(now, window);
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

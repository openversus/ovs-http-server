using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using OpenVersus.Server.Core.Missions;
using OpenVersus.Server.Core.Seasons;

namespace OpenVersus.Server.Core.Tests.Seasons;

/// <summary>attempt_daily_refresh: the TS server's answer, with the current season from the setting.</summary>
public sealed class SeasonServiceTests
{
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class StaticMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    [Fact]
    public void TheAnswerIsTheTsServersWithTheConfiguredSeason()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1790880000);
        var service = new SeasonService(new StaticMonitor<SeasonSettings>(new SeasonSettings { Current = "Season:SeasonSix" }),
            new StaticMonitor<MissionSettings>(new MissionSettings()), new FixedTime(now));

        var answer = service.DailyRefresh();

        // handlers/ssc.ts handleSsc_invoke_attempt_daily_refresh, with MVSTime(now) = 1790880000 and CurrentSeason set.
        var expected = JsonNode.Parse("""
            {"body":{"ServerTimeUtc":{"_hydra_unix_date":1790880000},"CurrentSeason":"Season:SeasonSix",
             "NextDailyRefreshTime":{"_hydra_unix_date":1790966400},"NextWeeklyRefreshTime":{"_hydra_unix_date":1791484800},
             "FreeCharacterRotation":[],"ReturnData":{},"PlayerMissionObject":{"MissionControllerContainers":{},"ClaimLocks":{}}},
             "metadata":null,"return_code":0}
            """);
        Assert.True(JsonNode.DeepEquals(expected, answer), answer.ToJsonString());
    }

    [Fact]
    public void WithMissionsTheRefreshTimesAreTheResets()
    {
        // Thursday 2026-10-01 05:00 UTC; resets at 11:00 UTC, weekly on Tuesdays.
        var now = new DateTimeOffset(2026, 10, 1, 5, 0, 0, TimeSpan.Zero);
        var missions = new MissionSettings { Enabled = true, ResetHourUtc = 11, WeeklyResetDay = DayOfWeek.Tuesday };
        var service = new SeasonService(new StaticMonitor<SeasonSettings>(new SeasonSettings()), new StaticMonitor<MissionSettings>(missions), new FixedTime(now));

        var body = service.DailyRefresh()["body"]!;
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 11, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(), body["NextDailyRefreshTime"]!["_hydra_unix_date"]!.GetValue<long>());
        Assert.Equal(new DateTimeOffset(2026, 10, 6, 11, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(), body["NextWeeklyRefreshTime"]!["_hydra_unix_date"]!.GetValue<long>());
    }

    [Fact]
    public void TheDefaultSeasonIsSix() => Assert.Equal("Season:SeasonSix", new SeasonSettings().Current);
}

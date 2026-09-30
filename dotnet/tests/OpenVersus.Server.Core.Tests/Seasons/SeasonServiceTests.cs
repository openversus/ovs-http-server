using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using OpenVersus.Server.Core.Seasons;

namespace OpenVersus.Server.Core.Tests.Seasons;

/// <summary>attempt_daily_refresh: the TS server's answer, with the current season from the setting.</summary>
public sealed class SeasonServiceTests
{
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class StaticMonitor(SeasonSettings value) : IOptionsMonitor<SeasonSettings>
    {
        public SeasonSettings CurrentValue => value;

        public SeasonSettings Get(string? name) => value;

        public IDisposable? OnChange(Action<SeasonSettings, string?> listener) => null;
    }

    [Fact]
    public void TheAnswerIsTheTsServersWithTheConfiguredSeason()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1790880000);
        var service = new SeasonService(new StaticMonitor(new SeasonSettings { Current = "Season:SeasonSix" }), new FixedTime(now));

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
    public void TheDefaultSeasonIsSix() => Assert.Equal("Season:SeasonSix", new SeasonSettings().Current);
}

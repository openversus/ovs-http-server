using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenVersus.Server.Core.Settings;

namespace OpenVersus.Server.Core.Seasons;

// POST /ssc/invoke/attempt_daily_refresh: the game asks at login which season is current and when the next daily and
// weekly refreshes are. The answer is the TS server's (handlers/ssc.ts, handleSsc_invoke_attempt_daily_refresh) field for
// field, with CurrentSeason from the setting Season:Current instead of its fixed "Season:SeasonFive". The game lists
// the rift seasons up to the current one (the Season 6 rogue rifts need Season:SeasonSix); other per-season data the
// servers still key by Season 5 (ranked, leaderboards, the login's trackers) may need filling for Season 6: see
// docs/SEASONS.md.

/// <summary>Season settings.</summary>
public sealed class SeasonSettings
{
    [Description("The season the game is told is current (attempt_daily_refresh CurrentSeason), a gameplay tag: Season:SeasonFive, Season:SeasonSix, ...")]
    [RegularExpression("^Season:Season[A-Za-z]+$")]
    public string Current { get; set; } = "Season:SeasonSix";
}

public interface ISeasonService
{
    /// <summary>The attempt_daily_refresh answer.</summary>
    JsonObject DailyRefresh();
}

internal sealed class SeasonService(IOptionsMonitor<SeasonSettings> settings, TimeProvider time) : ISeasonService
{
    public JsonObject DailyRefresh()
    {
        long now = time.GetUtcNow().ToUnixTimeSeconds();
        return new JsonObject
        {
            ["body"] = new JsonObject
            {
                ["ServerTimeUtc"] = new JsonObject { ["_hydra_unix_date"] = now },
                ["CurrentSeason"] = settings.CurrentValue.Current,
                ["NextDailyRefreshTime"] = new JsonObject { ["_hydra_unix_date"] = now + 86400 },
                ["NextWeeklyRefreshTime"] = new JsonObject { ["_hydra_unix_date"] = now + 604800 },
                ["FreeCharacterRotation"] = new JsonArray(),
                ["ReturnData"] = new JsonObject(),
                ["PlayerMissionObject"] = new JsonObject
                {
                    ["MissionControllerContainers"] = new JsonObject(),
                    ["ClaimLocks"] = new JsonObject(),
                },
            },
            ["metadata"] = null,
            ["return_code"] = 0,
        };
    }
}

public static class SeasonHosting
{
    public static WebApplicationBuilder AddSeasons(this WebApplicationBuilder builder)
    {
        builder.AddSetting<SeasonSettings>("Season");
        builder.Services.AddSingleton<ISeasonService, SeasonService>();
        return builder;
    }
}

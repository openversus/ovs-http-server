using System.ComponentModel;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace OpenVersus.Server.Core.Matchmaking;

// 1v1 Testing Grounds, the game's own event queue (evtq_1v1testinggrounds, matchmaking criteria EVT_2_Player_1, solo
// only), as OpenVersus runs it: unranked 1v1s with the Beta Speed mutator (the whole match at 1.2x), open whenever the
// FFA queue is not, so the menu's event slot is Testing Grounds on weekdays and Free For All on the weekend (FfaSchedule);
// closed while Twosday is on (its window, Tuesday evenings: every 1v1 goes to 2v2 then).
// The hiss shows the open one only (its feature toggles) and its Crc differs between the two, so a client takes the
// other catalog at its next login; a request outside the queue's window is refused, and its queued tickets are
// cancelled when it closes, as FFA's are.

/// <summary>1v1 Testing Grounds settings.</summary>
public sealed class TestingGroundsSettings
{
    [Description("1v1 Testing Grounds (unranked 1v1s with the Beta Speed mutator) is open whenever the FFA queue is closed: weekdays while Ffa:WeekendOnly is on, never while it is off. Off: never open, and the menu shows FFA all week as before.")]
    public bool Enabled { get; set; } = true;

    [Description("Open every day, FFA's weekend and Twosday included (the menu then shows Testing Grounds, not FFA): for testing, or an event. Off: weekdays outside Twosday only.")]
    public bool AlwaysOpen { get; set; }
}

public static class TestingGrounds
{
    /// <summary>The game's event queue slug.</summary>
    public const string EventQueue = "evtq_1v1testinggrounds";

    /// <summary>
    /// The matchmaking criteria the game sends for it: its MatchmakingCriteriaSlug with the retail suffix, as 1v1's
    /// 1v1-retail (POST /matches/matchmaking/EVT_2_Player_1-retail/request); the bare slug is taken too.
    /// </summary>
    public const string Criteria = "EVT_2_Player_1-retail", BareCriteria = "EVT_2_Player_1";

    /// <summary>Its tickets' list (matchType) and queue name.</summary>
    public const string List = "testinggrounds1v1";

    /// <summary>The mutator its matches have: Beta Speed (Matches.GameplayConfigs.BetaSpeedMutator).</summary>
    public const string Mutator = "ovs_beta_speed";

    public const string ScheduleText = "1v1 Testing Grounds (Beta Speed) is open Monday to Thursday, except during Twosday; Free For All takes its place on the weekend.";

    /// <summary>
    /// Whether the queue takes players at <paramref name="now"/>: enabled, and neither the FFA queue nor Twosday on (or
    /// always open).
    /// </summary>
    public static bool IsOpen(DateTimeOffset now, bool enabled, bool ffaWeekendOnly, bool alwaysOpen = false, bool twosday = false) =>
        enabled && (alwaysOpen || (!FfaSchedule.IsOpen(now, ffaWeekendOnly) && !twosday));

    /// <summary><see cref="IsOpen(DateTimeOffset, bool, bool, bool, bool)"/> with the service's settings and Twosday's switch (Redis).</summary>
    public static async Task<bool> IsOpenAsync(IServiceProvider services, DateTimeOffset now)
    {
        var settings = services.GetService<IOptionsMonitor<TestingGroundsSettings>>()?.CurrentValue ?? new TestingGroundsSettings();
        bool ffaWeekendOnly = services.GetService<IOptionsMonitor<FfaSettings>>()?.CurrentValue.WeekendOnly ?? new FfaSettings().WeekendOnly;
        if (!settings.Enabled || settings.AlwaysOpen || FfaSchedule.IsOpen(now, ffaWeekendOnly))
        {
            return IsOpen(now, settings.Enabled, ffaWeekendOnly, settings.AlwaysOpen);
        }

        return IsOpen(now, settings.Enabled, ffaWeekendOnly, settings.AlwaysOpen, await Twosday.IsActiveAsync(services, now));
    }

    /// <summary>Its match's notification fields beside the unranked ones: the mutator (the rollback registry's tick_rate reads it).</summary>
    public static JsonArray WorldBuffs() => new(Mutator);

    /// <summary>
    /// Merged over its match's config: unranked, not a custom game, the event queue, the mutator, and no progress: no
    /// XP or missions on the server (MissionService), none in the game.
    /// </summary>
    public static JsonObject ConfigOverride() => new()
    {
        ["bIsCustomGame"] = false,
        ["bModeGrantsProgress"] = false,
        ["EventQueueSlug"] = EventQueue,
        ["WorldBuffs"] = WorldBuffs(),
    };

    /// <summary>The answer to a request outside the window: a Hydra action failure, HTTP 200, as FFA's.</summary>
    public static JsonObject ClosedFailure() => new()
    {
        ["body"] = new JsonObject
        {
            ["error"] = "testing_grounds_queue_closed",
            ["ErrorCode"] = "QueueClosed",
            ["ErrorMessage"] = ScheduleText,
        },
        ["metadata"] = null,
        ["return_code"] = 1,
    };

    /// <summary>
    /// The hiss as the schedule has it at this moment: the open queue's feature toggle on and the other's off, and Testing
    /// Grounds always available while open (the catalog's FFA is), named for Beta Speed.
    /// </summary>
    public static void ApplyTo(JsonNode answer, bool open)
    {
        var data = answer["body"]?["Data"];
        if (data?["feature-toggles"]?["_hydra_compressed"] is JsonObject toggles)
        {
            toggles["FFA"] = !open;
            toggles[EventQueue] = open;
        }

        if (data?["event-queue-config"]?["_hydra_compressed"]?[EventQueue]?["data"] is JsonObject queue)
        {
            queue["bAlwaysAvailable"] = open;
        }

        if (data?["game-mode-config"]?["_hydra_compressed"]?["gm_1v1shields"]?["data"] is JsonObject mode)
        {
            // The queue's game mode requires shields; its matches run Beta Speed instead.
            if (mode["GameModeData"] is JsonObject gameMode)
            {
                gameMode["RequiredWorldBuffs"] = WorldBuffs();
            }

            if (mode["UIData"]?["DisplayName"] is JsonObject name)
            {
                name["localizations"] = new JsonObject { ["loc_Beta_Speed"] = "Beta Speed" };
            }
        }
    }
}

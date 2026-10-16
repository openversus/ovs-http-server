using System.ComponentModel;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace OpenVersus.Server.Core.Matchmaking;

// 1v1 Testing Grounds, the game's own event queue (evtq_1v1testinggrounds, matchmaking criteria EVT_2_Player_1, solo
// only), as OpenVersus runs it: OpenVersus's own mode, unranked 1v1s with its mutators (TestingGrounds:Mutators: Beta
// Speed, the whole match at BetaSpeed:Percent, and Shield HP, Smash-style shields), open whenever the FFA queue is not,
// so the menu's event slot is Testing Grounds on weekdays and Free For All on the weekend (FfaSchedule); closed while
// Twosday is on (its window, Tuesday evenings: every 1v1 goes to 2v2 then). A mutator that lives in a pak needs every
// player to have that exact pak: the release's paks, which a client that runs others is told to restart for
// (Clients/RequiredPaks.cs). Beta Speed needs every player's game to run the server's speed (checked against
// X-OVS-BetaSpeed), or the match desyncs.
// The hiss shows the open one only (its feature toggles) and its Crc differs between the two, so a client takes the
// other catalog at its next login; a request outside the queue's window is refused, and its queued tickets are
// cancelled when it closes, as FFA's are.

/// <summary>1v1 Testing Grounds settings.</summary>
public sealed class TestingGroundsSettings
{
    [Description("1v1 Testing Grounds (unranked 1v1s with the Beta Speed mutator) is open whenever the FFA queue is closed: the days not in Ffa:Days while Ffa:WeekendOnly is on, never while it is off. Off: never open, and the menu shows FFA all week as before.")]
    public bool Enabled { get; set; } = true;

    [Description("Open every day, FFA's weekend and Twosday included (the menu then shows Testing Grounds, not FFA): for testing, or an event. Off: weekdays outside Twosday only.")]
    public bool AlwaysOpen { get; set; }

    [Description("The mutators (world buff slugs) every Testing Grounds match runs, separated by commas. ovs_beta_speed also sets the match's rollback tick rate.")]
    public string Mutators { get; set; } = "ovs_beta_speed,ovs_shield_hp";
}

public static class TestingGrounds
{
    /// <summary>The game's event queue slug.</summary>
    public const string EventQueue = "evtq_1v1testinggrounds";

    /// <summary>The public FFA queue's event queue: its tile on the menu.</summary>
    public const string FfaEventQueue = "evtq_ffa";

    /// <summary>
    /// The matchmaking criteria the game sends for it: its MatchmakingCriteriaSlug with the retail suffix, as 1v1's
    /// 1v1-retail (POST /matches/matchmaking/EVT_2_Player_1-retail/request); the bare slug is taken too.
    /// </summary>
    public const string Criteria = "EVT_2_Player_1-retail", BareCriteria = "EVT_2_Player_1";

    /// <summary>Its tickets' list (matchType) and queue name.</summary>
    public const string List = "testinggrounds1v1";

    /// <summary>The mode its matches are listed with (ovsctl, the website's live matches): their own config says 1v1.</summary>
    public const string ListedMode = "1v1 Testing Grounds";

    /// <summary>The settings in force (defaults when the service has none).</summary>
    public static TestingGroundsSettings Of(IServiceProvider services) =>
        services.GetService<IOptionsMonitor<TestingGroundsSettings>>()?.CurrentValue ?? new TestingGroundsSettings();

    /// <summary>What a player turned away is told: its days (those not in Ffa:Days), and FFA's.</summary>
    public static string ScheduleText(FfaSettings ffa)
    {
        var ffaDays = FfaSchedule.DaysOf(ffa.Days);
        var days = Enum.GetValues<DayOfWeek>().Where(d => !ffaDays.Contains(d)).OrderBy(d => ((int)d + 6) % 7);
        return $"1v1 Testing Grounds is open {FfaSchedule.DayList(days)} (US Eastern), except during Twosday; Free For All takes its place on {FfaSchedule.DayList(ffaDays)}.";
    }

    /// <summary>The mutators of its matches, in order, without blanks or repeats.</summary>
    public static string[] MutatorsOf(TestingGroundsSettings settings) =>
        settings.Mutators.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToArray();

    /// <summary>
    /// Whether the settings' mutators include Beta Speed and <paramref name="header"/> (the client's X-OVS-BetaSpeed) does
    /// not report <paramref name="betaSpeed"/>'s speed: missing (an older client), or read before a change.
    /// </summary>
    public static bool WrongBetaSpeed(TestingGroundsSettings settings, Matches.BetaSpeedSettings betaSpeed, string? header) =>
        MutatorsOf(settings).Contains(Matches.GameplayConfigs.BetaSpeedMutator) && !Matches.BetaSpeed.Reports(header, betaSpeed.Percent);

    /// <summary>
    /// Whether the queue takes players at <paramref name="now"/>: enabled, and neither the FFA queue nor Twosday on (or
    /// always open).
    /// </summary>
    public static bool IsOpen(DateTimeOffset now, bool enabled, FfaSettings ffa, bool alwaysOpen = false, bool twosday = false) =>
        enabled && (alwaysOpen || (!FfaSchedule.IsOpen(now, ffa) && !twosday));

    private static FfaSettings Ffa(IServiceProvider services) => services.GetService<IOptionsMonitor<FfaSettings>>()?.CurrentValue ?? new FfaSettings();

    /// <summary><see cref="IsOpen(DateTimeOffset, bool, FfaSettings, bool, bool)"/> with the service's settings and Twosday's switch (Redis).</summary>
    public static async Task<bool> IsOpenAsync(IServiceProvider services, DateTimeOffset now)
    {
        var settings = Of(services);
        var ffa = Ffa(services);
        if (!settings.Enabled || settings.AlwaysOpen || FfaSchedule.IsOpen(now, ffa))
        {
            return IsOpen(now, settings.Enabled, ffa, settings.AlwaysOpen);
        }

        return IsOpen(now, settings.Enabled, ffa, settings.AlwaysOpen, await Twosday.IsActiveAsync(services, now));
    }

    /// <summary>
    /// Whether the menu shows the FFA tile: only while the FFA queue is open and Testing Grounds is not (Tuggernuts,
    /// 2026-10-13: during Twosday neither tile shows, then the open one comes back). With Testing Grounds off, FFA shows all
    /// week as before.
    /// </summary>
    public static bool FfaTileShown(DateTimeOffset now, bool testingGroundsOpen, bool enabled, FfaSettings ffa, bool twosday = false) =>
        !testingGroundsOpen && (!enabled || FfaSchedule.IsOpen(now, ffa, twosday));

    /// <summary><see cref="FfaTileShown(DateTimeOffset, bool, bool, FfaSettings, bool)"/> with the service's settings and Twosday's switch.</summary>
    public static async Task<bool> FfaTileShownAsync(IServiceProvider services, DateTimeOffset now, bool testingGroundsOpen) =>
        !testingGroundsOpen && (!Of(services).Enabled || await FfaSchedule.IsOpenAsync(services, now));

    /// <summary>Its match's notification fields beside the unranked ones: the mutators (the rollback registry's tick_rate reads them).</summary>
    public static JsonArray WorldBuffs(TestingGroundsSettings settings) => new(MutatorsOf(settings).Select(m => (JsonNode?)m).ToArray());

    /// <summary>
    /// Merged over its match's config: unranked, not a custom game, the event queue, the mutators, and no progress: no
    /// XP or missions on the server (MissionService), none in the game.
    /// </summary>
    public static JsonObject ConfigOverride(TestingGroundsSettings settings) => new()
    {
        ["bIsCustomGame"] = false,
        ["bModeGrantsProgress"] = false,
        ["EventQueueSlug"] = EventQueue,
        ["WorldBuffs"] = WorldBuffs(settings),
    };

    /// <summary>The answer to a request outside the window: a Hydra action failure, HTTP 200, as FFA's.</summary>
    public static JsonObject ClosedFailure(FfaSettings ffa) => Failure("testing_grounds_queue_closed", "QueueClosed", ScheduleText(ffa));

    /// <summary>The answer to a player whose game runs another Beta Speed (<see cref="WrongBetaSpeed"/>): restart the game to update.</summary>
    public static JsonObject BetaSpeedFailure() => Failure("testing_grounds_beta_speed_changed", "BetaSpeedChanged",
        "Beta Speed changed: restart the game to update, then queue again.");

    private static JsonObject Failure(string error, string code, string message) => new()
    {
        ["body"] = new JsonObject { ["error"] = error, ["ErrorCode"] = code, ["ErrorMessage"] = message },
        ["metadata"] = null,
        ["return_code"] = 1,
    };

    /// <summary>
    /// The hiss as the schedule has it at this moment: Testing Grounds' feature toggle on while it is open, FFA's while
    /// <paramref name="ffaTile"/> (<see cref="FfaTileShown(DateTimeOffset, bool, bool, bool)"/>: neither during Twosday), and
    /// Testing Grounds always available while open (the catalog's FFA is), run with the settings' mutators.
    /// </summary>
    public static void ApplyTo(JsonNode answer, bool open, TestingGroundsSettings settings, bool ffaTile)
    {
        var data = answer["body"]?["Data"];
        if (data?["feature-toggles"]?["_hydra_compressed"] is JsonObject toggles)
        {
            toggles["FFA"] = ffaTile;
            toggles[FfaEventQueue] = ffaTile;
            toggles[EventQueue] = open;
        }

        if (data?["event-queue-config"]?["_hydra_compressed"]?[EventQueue]?["data"] is JsonObject queue)
        {
            queue["bAlwaysAvailable"] = open;
        }

        // The FFA tile is its event queue, always available in the catalog: the FFA toggle alone leaves the tile on the menu
        // (Lab 2026-10-13, Twosday). Hidden the way Testing Grounds' is: its toggle and its availability.
        if (data?["event-queue-config"]?["_hydra_compressed"]?[FfaEventQueue]?["data"] is JsonObject ffaQueue)
        {
            ffaQueue["bAlwaysAvailable"] = ffaTile;
        }

        if (data?["game-mode-config"]?["_hydra_compressed"]?["gm_1v1shields"]?["data"] is JsonObject mode)
        {
            // The queue's game mode requires the game's shields buff; its matches run our mutators instead (shields are on
            // in every OpenVersus match: CustomGameSettings.bShieldsEnabled).
            if (mode["GameModeData"] is JsonObject gameMode)
            {
                gameMode["RequiredWorldBuffs"] = WorldBuffs(settings);
            }
        }
    }
}

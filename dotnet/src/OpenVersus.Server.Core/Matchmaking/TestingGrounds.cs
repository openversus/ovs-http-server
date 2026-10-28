using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace OpenVersus.Server.Core.Matchmaking;

// Testing Grounds, the game's own event queues, as OpenVersus runs them: unranked matches with OpenVersus's mutators, no
// missions; a player's first TestingGrounds:DailyXpGames games a day pay TestingGrounds:XpPercent of an FFA game's End
// Game XP (MatchResultStream; Tuggernuts, 2026-10-27: 10 games at half, so it cannot be farmed). Two of them
// (Tuggernuts, 2026-10-27, for the Season 1 launch):
//   1v1  evtq_1v1testinggrounds, matchmaking criteria EVT_2_Player_1, solo only; TestingGrounds:Mutators (Beta Speed, the
//        whole match at BetaSpeed:Percent, and Shield HP, Smash-style shields). Open every day.
//   2v2  evtq_2v2testinggrounds, criteria EVT_4_Player_2, parties of two welcome (the game's bPartyEligible);
//        TestingGrounds:TwoVsTwoMutators (Beta Speed, Shield HP, Individual Stocks and Friendly Fire). It shares the menu's
//        event slot with Free For All: open on the days FFA is not (FfaSchedule, Ffa:Days while Ffa:WeekendOnly is on).
//        With Individual Stocks each fighter has TestingGrounds:TwoVsTwoLives lives (Tuggernuts, 2026-10-27: 6): the match's
//        ringouts to win are twice that (customNumRingouts), and the client gives each fighter half of them.
// Both close while Twosday is on (its window, Tuesday evenings: every 1v1 goes to 2v2 then), as FFA does. A mutator that
// lives in a pak needs every player to have that exact pak: the release's paks, which a client that runs others is told
// to restart for (Clients/RequiredPaks.cs). Beta Speed needs every player's game to run the server's speed (checked
// against the requester's X-OVS-BetaSpeed), or the match desyncs.
// The hiss shows the open ones only (their feature toggles) and its Crc differs with every combination (CrcOffset), so a
// client takes the new catalog at its next login; a request outside a queue's window is refused, and its queued tickets
// are cancelled when it closes, as FFA's are.

/// <summary>Testing Grounds settings.</summary>
public sealed class TestingGroundsSettings
{
    [Description("Testing Grounds (unranked matches with OpenVersus's mutators): 1v1 every day, 2v2 on the days not in Ffa:Days (while Ffa:WeekendOnly is on), neither during Twosday. Off: neither is ever open, and the menu shows FFA all week.")]
    public bool Enabled { get; set; } = true;

    [Description("Open every day, FFA's days and Twosday included (the menu then shows 2v2 Testing Grounds, not FFA): for testing, or an event. Off: on their days, outside Twosday.")]
    public bool AlwaysOpen { get; set; }

    [Description("The mutators (world buff slugs) every 1v1 Testing Grounds match runs, separated by commas. ovs_beta_speed also sets the match's rollback tick rate.")]
    public string Mutators { get; set; } = "ovs_beta_speed,ovs_shield_hp";

    [Description("2v2 Testing Grounds: on its days (those FFA is not) its tile takes FFA's place. Off: FFA's tile shows all week, as before it existed; 1v1 Testing Grounds is unaffected.")]
    public bool TwoVsTwoEnabled { get; set; } = true;

    [Description("The mutators every 2v2 Testing Grounds match runs, separated by commas.")]
    public string TwoVsTwoMutators { get; set; } = "ovs_beta_speed,ovs_shield_hp,ovs_2v2_individual_stocks,ovs_friendly_fire";

    [Description("The lives each fighter has in a 2v2 Testing Grounds match while its mutators include Individual Stocks (ovs_2v2_individual_stocks): the match is played to twice this many ringouts. Without Individual Stocks the match keeps 2v2's 4 shared ringouts.")]
    [Range(1, 49)]
    public int TwoVsTwoLives { get; set; } = 6;

    [Description("The Testing Grounds games (1v1 and 2v2 together) each player is paid End Game's XP for every day, 00:00 to 24:00 US Eastern: TestingGrounds:XpPercent of what a public FFA game pays (the battle pass, account and character levels; the winners the win bonus). Later games that day pay nothing. Missions never progress there. 0: no XP.")]
    [Range(0, 1000)]
    public int DailyXpGames { get; set; } = 10;

    [Description("How much of a public FFA game's XP a Testing Grounds game pays, in percent (Tuggernuts, 2026-10-27: 50, half), rounded down; only within TestingGrounds:DailyXpGames.")]
    [Range(0, 100)]
    public int XpPercent { get; set; } = 50;
}

public static class TestingGrounds
{
    /// <summary>One of the two queues: the game's names for it and ours.</summary>
    /// <param name="EventQueue">The game's event queue slug.</param>
    /// <param name="Criteria">
    /// The matchmaking criteria the game sends for it: its MatchmakingCriteriaSlug with the retail suffix, as 1v1's
    /// 1v1-retail (POST /matches/matchmaking/EVT_2_Player_1-retail/request); <paramref name="BareCriteria"/> is taken too.
    /// </param>
    /// <param name="List">Its tickets' list (matchType) and queue name.</param>
    /// <param name="ListedMode">The mode its matches are listed with (ovsctl, the website's live matches).</param>
    /// <param name="GameMode">The game mode its catalog entry plays (the game's own shields mode).</param>
    /// <param name="Mode">The matchmaker's mode for its matches.</param>
    public sealed record Queue(string EventQueue, string Criteria, string BareCriteria, string List, string ListedMode, string GameMode, string Mode);

    public static readonly Queue OneVsOne = new("evtq_1v1testinggrounds", "EVT_2_Player_1-retail", "EVT_2_Player_1", "testinggrounds1v1",
        "1v1 Testing Grounds", "gm_1v1shields", "1v1");

    public static readonly Queue TwoVsTwo = new("evtq_2v2testinggrounds", "EVT_4_Player_2-retail", "EVT_4_Player_2", "testinggrounds2v2",
        "2v2 Testing Grounds", "gm_2v2shields", "2v2");

    /// <summary>1v1's event queue slug.</summary>
    public const string EventQueue = "evtq_1v1testinggrounds";

    /// <summary>The public FFA queue's event queue: its tile on the menu.</summary>
    public const string FfaEventQueue = "evtq_ffa";

    /// <summary>1v1's criteria, with and without the retail suffix.</summary>
    public const string Criteria = "EVT_2_Player_1-retail", BareCriteria = "EVT_2_Player_1";

    /// <summary>1v1's tickets' list (matchType) and queue name.</summary>
    public const string List = "testinggrounds1v1";

    /// <summary>The mode 1v1's matches are listed with (ovsctl, the website's live matches): their own config says 1v1.</summary>
    public const string ListedMode = "1v1 Testing Grounds";

    /// <summary>The settings in force (defaults when the service has none).</summary>
    public static TestingGroundsSettings Of(IServiceProvider services) =>
        services.GetService<IOptionsMonitor<TestingGroundsSettings>>()?.CurrentValue ?? new TestingGroundsSettings();

    /// <summary>The queue of a ticket list, or null when it is neither's.</summary>
    public static Queue? ByList(string? list) => list == OneVsOne.List ? OneVsOne : list == TwoVsTwo.List ? TwoVsTwo : null;

    /// <summary>The queue whose event queue a match's config names, or null.</summary>
    public static Queue? ByEventQueue(string? eventQueue) =>
        eventQueue == OneVsOne.EventQueue ? OneVsOne : eventQueue == TwoVsTwo.EventQueue ? TwoVsTwo : null;

    /// <summary>What the menu shows at a moment: which Testing Grounds queues are open, and whether FFA's tile shows.</summary>
    public readonly record struct Schedule(bool OneVsOne, bool TwoVsTwo, bool FfaTile)
    {
        /// <summary>Whether <paramref name="queue"/> takes players.</summary>
        public bool IsOpen(Queue queue) => queue == TestingGrounds.TwoVsTwo ? TwoVsTwo : OneVsOne;

        /// <summary>
        /// Added to the hiss Crc, so no two catalogs share one (HissService.CurrentCrcAsync). Each under 200,000 and none
        /// 200,000 apart, so no sum with Arenas' offsets (200,000 plus 400,000 a second) meets another.
        /// </summary>
        public double CrcOffset => (OneVsOne, TwoVsTwo, FfaTile) switch
        {
            (false, false, true) => 0,
            (false, false, false) => 50_000,
            (false, true, _) => 75_000,
            (true, false, true) => 125_000,
            (true, true, _) => 150_000,
            (true, false, false) => 175_000,
        };
    }

    /// <summary>
    /// The schedule at <paramref name="now"/>: 1v1 open every day outside <paramref name="twosday"/>, 2v2 on the days
    /// FFA is not, outside it (both every day while AlwaysOpen); FFA's tile while 2v2 is not open and the FFA queue is
    /// (Tuggernuts, 2026-10-13: during Twosday no tile shows), or all week with 2v2 off, as before it existed.
    /// </summary>
    public static Schedule ScheduleAt(DateTimeOffset now, TestingGroundsSettings settings, FfaSettings ffa, bool twosday = false)
    {
        bool oneVsOne = settings.Enabled && (settings.AlwaysOpen || !twosday);
        bool twoVsTwoOn = settings.Enabled && settings.TwoVsTwoEnabled;
        bool twoVsTwo = twoVsTwoOn && (settings.AlwaysOpen || (!FfaSchedule.IsOpen(now, ffa) && !twosday));
        bool ffaTile = !twoVsTwo && (!twoVsTwoOn || FfaSchedule.IsOpen(now, ffa, twosday));
        return new Schedule(oneVsOne, twoVsTwo, ffaTile);
    }

    /// <summary><see cref="ScheduleAt"/> with the service's settings and Twosday's switch (Redis).</summary>
    public static async Task<Schedule> ScheduleAsync(IServiceProvider services, DateTimeOffset now) =>
        ScheduleAt(now, Of(services), services.GetService<IOptionsMonitor<FfaSettings>>()?.CurrentValue ?? new FfaSettings(),
            await Twosday.IsActiveAsync(services, now));

    /// <summary>Whether 1v1 Testing Grounds takes players at <paramref name="now"/>.</summary>
    public static async Task<bool> IsOpenAsync(IServiceProvider services, DateTimeOffset now) => (await ScheduleAsync(services, now)).OneVsOne;

    /// <summary>Whether <paramref name="queue"/> takes players at <paramref name="now"/>.</summary>
    public static async Task<bool> IsOpenAsync(IServiceProvider services, DateTimeOffset now, Queue queue) => (await ScheduleAsync(services, now)).IsOpen(queue);

    /// <summary>What a player turned away is told: its days, and FFA's for 2v2.</summary>
    public static string ScheduleText(Queue queue, FfaSettings ffa)
    {
        if (queue != TwoVsTwo)
        {
            return "1v1 Testing Grounds is open every day, except during Twosday.";
        }

        var ffaDays = FfaSchedule.DaysOf(ffa.Days);
        var days = Enum.GetValues<DayOfWeek>().Where(d => !ffaDays.Contains(d)).OrderBy(d => ((int)d + 6) % 7);
        return $"2v2 Testing Grounds is open {FfaSchedule.DayList(days)} (US Eastern), except during Twosday; Free For All takes its place on {FfaSchedule.DayList(ffaDays)}.";
    }

    /// <summary>The mutators of <paramref name="queue"/>'s matches, in order, without blanks or repeats.</summary>
    public static string[] MutatorsOf(TestingGroundsSettings settings, Queue queue) =>
        (queue == TwoVsTwo ? settings.TwoVsTwoMutators : settings.Mutators)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToArray();

    /// <summary>1v1's mutators.</summary>
    public static string[] MutatorsOf(TestingGroundsSettings settings) => MutatorsOf(settings, OneVsOne);

    /// <summary>
    /// Whether <paramref name="queue"/>'s mutators include Beta Speed and <paramref name="header"/> (the client's
    /// X-OVS-BetaSpeed) does not report <paramref name="betaSpeed"/>'s speed: missing (an older client), or read before a change.
    /// </summary>
    public static bool WrongBetaSpeed(TestingGroundsSettings settings, Queue queue, Matches.BetaSpeedSettings betaSpeed, string? header) =>
        MutatorsOf(settings, queue).Contains(Matches.GameplayConfigs.BetaSpeedMutator) && !Matches.BetaSpeed.Reports(header, betaSpeed.Percent);

    /// <summary>1v1's <see cref="WrongBetaSpeed(TestingGroundsSettings, Queue, Matches.BetaSpeedSettings, string?)"/>.</summary>
    public static bool WrongBetaSpeed(TestingGroundsSettings settings, Matches.BetaSpeedSettings betaSpeed, string? header) =>
        WrongBetaSpeed(settings, OneVsOne, betaSpeed, header);

    /// <summary>Its match's notification fields beside the unranked ones: the mutators (the rollback registry's tick_rate reads them).</summary>
    public static JsonArray WorldBuffs(TestingGroundsSettings settings, Queue queue) =>
        new(MutatorsOf(settings, queue).Select(m => (JsonNode?)m).ToArray());

    /// <summary>The Individual Stocks mutator's slug (the client's StockRules.IndividualStocksSlug).</summary>
    public const string IndividualStocksMutator = "ovs_2v2_individual_stocks";

    /// <summary>
    /// The ringouts to win <paramref name="queue"/>'s matches when the queue sets them (its notification's customNumRingouts):
    /// 2v2 with Individual Stocks, twice TwoVsTwoLives; otherwise null, the mode's own.
    /// </summary>
    public static int? RingoutsOf(TestingGroundsSettings settings, Queue queue) =>
        queue == TwoVsTwo && MutatorsOf(settings, queue).Contains(IndividualStocksMutator) ? settings.TwoVsTwoLives * 2 : null;

    /// <summary>1v1's world buffs.</summary>
    public static JsonArray WorldBuffs(TestingGroundsSettings settings) => WorldBuffs(settings, OneVsOne);

    /// <summary>
    /// Merged over its match's config: unranked, not a custom game, the event queue, the mutators, and no progress: no
    /// match XP or missions on the server (MissionService), none in the game. Its End Game XP is MatchResultStream's, by
    /// the event queue.
    /// </summary>
    public static JsonObject ConfigOverride(TestingGroundsSettings settings, Queue queue) => new()
    {
        ["bIsCustomGame"] = false,
        ["bModeGrantsProgress"] = false,
        ["EventQueueSlug"] = queue.EventQueue,
        ["WorldBuffs"] = WorldBuffs(settings, queue),
    };

    /// <summary>1v1's config override.</summary>
    public static JsonObject ConfigOverride(TestingGroundsSettings settings) => ConfigOverride(settings, OneVsOne);

    /// <summary>The answer to a request outside the window: a Hydra action failure, HTTP 200, as FFA's.</summary>
    public static JsonObject ClosedFailure(Queue queue, FfaSettings ffa) => Failure("testing_grounds_queue_closed", "QueueClosed", ScheduleText(queue, ffa));

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
    /// The hiss as the schedule has it at this moment: each Testing Grounds queue's feature toggle on while it is open
    /// and FFA's while its tile shows, each always available while open (the catalog's FFA is), run with its mutators.
    /// </summary>
    public static void ApplyTo(JsonNode answer, Schedule schedule, TestingGroundsSettings settings)
    {
        var data = answer["body"]?["Data"];
        if (data?["feature-toggles"]?["_hydra_compressed"] is JsonObject toggles)
        {
            toggles["FFA"] = schedule.FfaTile;
            toggles[FfaEventQueue] = schedule.FfaTile;
            toggles[OneVsOne.EventQueue] = schedule.OneVsOne;
            toggles[TwoVsTwo.EventQueue] = schedule.TwoVsTwo;
        }

        var queues = data?["event-queue-config"]?["_hydra_compressed"];
        foreach (var queue in new[] { OneVsOne, TwoVsTwo })
        {
            if (queues?[queue.EventQueue]?["data"] is JsonObject entry)
            {
                entry["bAlwaysAvailable"] = schedule.IsOpen(queue);
            }

            // The queue's game mode requires the game's shields buff; its matches run our mutators instead (shields are on
            // in every OpenVersus match: CustomGameSettings.bShieldsEnabled).
            if (data?["game-mode-config"]?["_hydra_compressed"]?[queue.GameMode]?["data"]?["GameModeData"] is JsonObject gameMode)
            {
                gameMode["RequiredWorldBuffs"] = WorldBuffs(settings, queue);
            }
        }

        // The FFA tile is its event queue, always available in the catalog: the FFA toggle alone leaves the tile on the menu
        // (Lab 2026-10-13, Twosday). Hidden the way Testing Grounds' is: its toggle and its availability.
        if (queues?[FfaEventQueue]?["data"] is JsonObject ffaQueue)
        {
            ffaQueue["bAlwaysAvailable"] = schedule.FfaTile;
        }
    }
}

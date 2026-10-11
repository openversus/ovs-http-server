using System.ComponentModel;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace OpenVersus.Server.Core.Matchmaking;

// 1v1 Testing Grounds, the game's own event queue (evtq_1v1testinggrounds, matchmaking criteria EVT_2_Player_1, solo
// only), as OpenVersus runs it: OpenVersus's own mode, unranked 1v1s with its mutators (TestingGrounds:Mutators: Beta
// Speed, the whole match at 1.1x, and Shield HP, Smash-style shields), open whenever the FFA queue is not, so the menu's
// event slot is Testing Grounds on weekdays and Free For All on the weekend (FfaSchedule); closed while Twosday is on
// (its window, Tuesday evenings: every 1v1 goes to 2v2 then). A mutator that lives in a pak needs every player to have
// that exact pak (TestingGrounds:RequiredPaks, checked against the client's X-OVS-Paks header), or the match desyncs.
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

    [Description("The mutators (world buff slugs) every Testing Grounds match runs, separated by commas. ovs_beta_speed also sets the match's rollback tick rate.")]
    public string Mutators { get; set; } = "ovs_beta_speed,ovs_shield_hp";

    [Description("The paks a player must have, exactly, to queue: Name=sha256 pairs separated by semicolons, the SHA-256 of the pak's .utoc (the client's X-OVS-Paks header reports its OVS_Experimental paks the same way). Empty: no requirement. A request missing one is refused with a message to restart the game and update.")]
    public string RequiredPaks { get; set; } = "";
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

    /// <summary>The client's report of its paks (name=sha256 of the .utoc, separated by semicolons).</summary>
    public const string PaksHeader = "X-OVS-Paks";

    /// <summary>The settings in force (defaults when the service has none).</summary>
    public static TestingGroundsSettings Of(IServiceProvider services) =>
        services.GetService<IOptionsMonitor<TestingGroundsSettings>>()?.CurrentValue ?? new TestingGroundsSettings();

    public const string ScheduleText = "1v1 Testing Grounds is open Monday to Thursday, except during Twosday; Free For All takes its place on the weekend.";

    /// <summary>The mutators of its matches, in order, without blanks or repeats.</summary>
    public static string[] MutatorsOf(TestingGroundsSettings settings) =>
        settings.Mutators.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToArray();

    /// <summary>
    /// The first pak the settings require that <paramref name="header"/> (the client's X-OVS-Paks) does not report with
    /// the same hash, or null when every required pak is there (or none is required).
    /// </summary>
    public static string? MissingPak(TestingGroundsSettings settings, string? header)
    {
        var have = Pairs(header);
        foreach (var (name, hash) in Pairs(settings.RequiredPaks))
        {
            if (!have.TryGetValue(name, out var theirs) || !string.Equals(theirs, hash, StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }

        return null;
    }

    private static Dictionary<string, string> Pairs(string? text)
    {
        var pairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string item in (text ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = item.IndexOf('=');
            if (eq > 0)
            {
                pairs[item[..eq].Trim()] = item[(eq + 1)..].Trim();
            }
        }

        return pairs;
    }

    /// <summary>
    /// Whether the queue takes players at <paramref name="now"/>: enabled, and neither the FFA queue nor Twosday on (or
    /// always open).
    /// </summary>
    public static bool IsOpen(DateTimeOffset now, bool enabled, bool ffaWeekendOnly, bool alwaysOpen = false, bool twosday = false) =>
        enabled && (alwaysOpen || (!FfaSchedule.IsOpen(now, ffaWeekendOnly) && !twosday));

    /// <summary><see cref="IsOpen(DateTimeOffset, bool, bool, bool, bool)"/> with the service's settings and Twosday's switch (Redis).</summary>
    public static async Task<bool> IsOpenAsync(IServiceProvider services, DateTimeOffset now)
    {
        var settings = Of(services);
        bool ffaWeekendOnly = services.GetService<IOptionsMonitor<FfaSettings>>()?.CurrentValue.WeekendOnly ?? new FfaSettings().WeekendOnly;
        if (!settings.Enabled || settings.AlwaysOpen || FfaSchedule.IsOpen(now, ffaWeekendOnly))
        {
            return IsOpen(now, settings.Enabled, ffaWeekendOnly, settings.AlwaysOpen);
        }

        return IsOpen(now, settings.Enabled, ffaWeekendOnly, settings.AlwaysOpen, await Twosday.IsActiveAsync(services, now));
    }

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
    public static JsonObject ClosedFailure() => Failure("testing_grounds_queue_closed", "QueueClosed", ScheduleText);

    /// <summary>The answer to a player without a required pak (<see cref="MissingPak"/>): restart the game to update.</summary>
    public static JsonObject PakFailure() => Failure("testing_grounds_pak_required", "PakRequired",
        "Testing Grounds needs this week's OpenVersus experimental pak. Restart the game to update it, then queue again.");

    private static JsonObject Failure(string error, string code, string message) => new()
    {
        ["body"] = new JsonObject { ["error"] = error, ["ErrorCode"] = code, ["ErrorMessage"] = message },
        ["metadata"] = null,
        ["return_code"] = 1,
    };

    /// <summary>
    /// The hiss as the schedule has it at this moment: the open queue's feature toggle on and the other's off, and Testing
    /// Grounds always available while open (the catalog's FFA is), run with the settings' mutators.
    /// </summary>
    public static void ApplyTo(JsonNode answer, bool open, TestingGroundsSettings settings)
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
            // The queue's game mode requires the game's shields buff; its matches run our mutators instead (shields are on
            // in every OpenVersus match: CustomGameSettings.bShieldsEnabled).
            if (mode["GameModeData"] is JsonObject gameMode)
            {
                gameMode["RequiredWorldBuffs"] = WorldBuffs(settings);
            }
        }
    }
}

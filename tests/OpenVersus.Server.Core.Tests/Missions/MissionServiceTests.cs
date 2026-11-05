using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Missions;

namespace OpenVersus.Server.Core.Tests.Missions;

/// <summary>
/// Each player's missions: the roll rules read off WB's own object (docs/MISSIONS.md), the resets, and the store (real
/// Mongo, a database of its own, dropped: OVS_TEST_MONGO).
/// </summary>
public sealed class MissionServiceTests : IAsyncLifetime
{
    private const string TestMongoDb = "ovs_missions_tests";
    private static readonly string? s_mongo = Environment.GetEnvironmentVariable("OVS_TEST_MONGO");

    private sealed class FixedTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 1, 5, 0, 0, TimeSpan.Zero); // a Thursday

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Monitor(MissionSettings value) : IOptionsMonitor<MissionSettings>
    {
        public MissionSettings CurrentValue { get; set; } = value;

        public MissionSettings Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<MissionSettings, string?> listener) => null;
    }

    // Draws in order from a script, then 0; GUIDs counted up.
    private sealed class ScriptedRandom(params int[] draws) : MissionRandom
    {
        private readonly Queue<int> _draws = new(draws);
        private int _guid;

        public override int Next(int maxExclusive) => _draws.TryDequeue(out int d) ? d % maxExclusive : 0;

        public override Guid NewGuid() => new($"00000000-0000-0000-0000-{++_guid:x12}");
    }

    private readonly FixedTime _time = new();
    private readonly MissionSettings _settings = new() { Enabled = true, ResetHourUtc = 11, WeeklyResetDay = DayOfWeek.Tuesday };
    private IMongoClient? _mongo;

    private MissionService Service(MissionRandom? random = null)
    {
        var services = new ServiceCollection();
        if (_mongo is not null)
        {
            services.AddSingleton(_mongo.GetDatabase(TestMongoDb));
        }

        return new MissionService(services.BuildServiceProvider(), new Monitor(_settings), _time, random ?? new ScriptedRandom(), NullLogger<MissionService>.Instance);
    }

    public async Task InitializeAsync()
    {
        if (!string.IsNullOrEmpty(s_mongo))
        {
            _mongo = new MongoClient(s_mongo);
            await _mongo.DropDatabaseAsync(TestMongoDb);
        }
    }

    public async Task DisposeAsync()
    {
        if (_mongo is not null)
        {
            await _mongo.DropDatabaseAsync(TestMongoDb);
        }
    }

    private static JsonObject Entry(string mission, int weight, bool force = false) =>
        new() { ["Mission"] = mission, ["Weight"] = weight, ["bForce"] = force };

    private static JsonObject Controller(MissionState state, string container, string controller) =>
        state.ServerData["MissionControllerContainers"]![container]!["MissionControllers"]![controller]!.AsObject();

    [Fact]
    public void TheDefaultContainersAreTheSeasonFiveBattlePassAndEveryCharacter()
    {
        var live = MissionService.LiveContainers(new MissionSettings().Containers);
        Assert.Equal(["miscon_battlepassdaily_s5", "miscon_battlepassweekly_s5"], live.Take(2));
        Assert.Equal(33, live.Count(c => c.StartsWith("miscon_unlockable_", StringComparison.Ordinal)));
        Assert.Equal(35, live.Count);
        Assert.Empty(MissionService.LiveContainers("miscon_nope, miscon_nope_*"));
    }

    [Fact]
    public void DescendingOrderTakesTheHeaviestForcedFirstAndDoesNotSkipUsed()
    {
        List<JsonObject> list = [Entry("a", 100), Entry("b", 800), Entry("c", 800), Entry("d", 500), Entry("e", 50, force: true)];
        var picks = Service().Pick(list, "DescendingOrderByWeight", 3, ["b", "c"]);
        Assert.Equal(["e", "b", "c"], picks);
    }

    [Fact]
    public void RandomByWeightDrawsUnusedFirstWithoutRepeats()
    {
        List<JsonObject> list = [Entry("a", 100), Entry("b", 100), Entry("c", 100), Entry("d", 100)];
        // a used: the pool is b, c, d; draws 150 (-> c, of 300) then 0 (-> b).
        var picks = Service(new ScriptedRandom(150, 0)).Pick(list, "RandomByWeight", 2, ["a"]);
        Assert.Equal(["c", "b"], picks);
        // Everything used: all of them again.
        Assert.Equal(2, Service().Pick(list, "RandomByWeight", 2, ["a", "b", "c", "d"]).Distinct().Count());
        // Fewer than Count: all there are.
        Assert.Equal(4, Service().Pick(list, "RandomByWeight", 9, []).Count);
    }

    [Fact]
    public void UnlockableGrantsTheWholeList()
    {
        List<JsonObject> list = [Entry("a", 0), Entry("b", 0), Entry("c", 0)];
        Assert.Equal(["a", "b", "c"], Service().Pick(list, "Unlockable", 50, []));
    }

    [Fact]
    public void AFirstRollGrantsEveryContainerInTheWbShape()
    {
        var state = MissionState.New(_time.Now);
        var live = MissionService.LiveContainers(_settings.Containers);
        Assert.True(Service().Roll(state, live, _settings, _time.Now));

        // Dailies: one group of one mission per controller; weekly: eight; a character: one group of all five.
        foreach (var (_, controller) in state.ServerData["MissionControllerContainers"]!["miscon_battlepassdaily_s5"]!["MissionControllers"]!.AsObject())
        {
            Assert.Single(controller!["Missions"]!.AsArray());
            Assert.Single(controller["Missions"]![0]!.AsObject());
        }

        Assert.Equal(8, Controller(state, "miscon_battlepassweekly_s5", "misctl_battlepass_weekly_new")["Missions"]!.AsArray().Count);
        var character = Controller(state, "miscon_unlockable_c020", "misctl_unlockable_c020");
        Assert.Single(character["Missions"]!.AsArray());
        Assert.Equal(5, character["Missions"]![0]!.AsObject().Count);
        Assert.Equal(5, character["UsedMissions"]!.AsArray().Count);

        // A mission as WB's object holds one: its objectives at 0, then its GUID; the same objectives as WB's entry.
        var mission = character["Missions"]![0]!["mis_dealalldamage_c020"]!.AsObject();
        Assert.Equal(["MissionObjectives", "MissionGuid"], mission.Select(kv => kv.Key));
        var wb = MissionObject.Answer("x", enabled: true)["body"]!["server_data"]!["MissionControllerContainers"]!["miscon_unlockable_c020"]!
            ["MissionControllers"]!["misctl_unlockable_c020"]!["Missions"]![0]!["mis_dealalldamage_c020"]!["MissionObjectives"]!;
        Assert.Equal(wb.AsArray().Select(o => o!["Slug"]!.GetValue<string>()), mission["MissionObjectives"]!.AsArray().Select(o => o!["Slug"]!.GetValue<string>()));
        Assert.All(mission["MissionObjectives"]!.AsArray(), o => Assert.Equal(0, o!["Progress"]!.GetValue<int>()));
    }

    [Fact]
    public void ResetsAddMissionsAndKeepTheUnfinished()
    {
        var state = MissionState.New(_time.Now);
        var live = MissionService.LiveContainers(_settings.Containers);
        var service = Service();
        service.Roll(state, live, _settings, _time.Now);
        int Groups(string container, string controller) => Controller(state, container, controller)["Missions"]!.AsArray().Count;

        // Before the daily reset (11:00): nothing is due.
        Assert.False(service.Roll(state, live, _settings, _time.Now.AddHours(5)));

        // After it: each daily controller gets one more; the weekly and the characters do not.
        Assert.True(service.Roll(state, live, _settings, _time.Now.AddHours(7)));
        Assert.Equal(2, Groups("miscon_battlepassdaily_s5", "misctl_battlepass_daily_new_1"));
        Assert.Equal(8, Groups("miscon_battlepassweekly_s5", "misctl_battlepass_weekly_new"));
        Assert.Single(Controller(state, "miscon_unlockable_c020", "misctl_unlockable_c020")["Missions"]!.AsArray());

        // Several days later, once: one more daily, not one per day passed; and past Tuesday 11:00, eight more weekly.
        Assert.True(service.Roll(state, live, _settings, new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero)));
        Assert.Equal(3, Groups("miscon_battlepassdaily_s5", "misctl_battlepass_daily_new_1"));
        Assert.Equal(16, Groups("miscon_battlepassweekly_s5", "misctl_battlepass_weekly_new"));
        Assert.Equal(16, Controller(state, "miscon_battlepassweekly_s5", "misctl_battlepass_weekly_new")["UsedMissions"]!.AsArray().Count);
    }

    [Theory]
    [InlineData(2026, 10, 1, 10, 59, 2026, 9, 30, 2026, 9, 29)] // Thursday before the reset
    [InlineData(2026, 10, 1, 11, 0, 2026, 10, 1, 2026, 9, 29)]  // at it
    [InlineData(2026, 10, 6, 11, 0, 2026, 10, 6, 2026, 10, 6)]  // Tuesday at it
    [InlineData(2026, 10, 6, 10, 0, 2026, 10, 5, 2026, 9, 29)]  // Tuesday before it
    public void TheResetsAreAtTheConfiguredTime(int y, int mo, int d, int h, int mi, int dy, int dmo, int dd, int wy, int wmo, int wd)
    {
        var now = new DateTimeOffset(y, mo, d, h, mi, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(dy, dmo, dd, 11, 0, 0, TimeSpan.Zero), MissionClock.LastDaily(_settings, now));
        Assert.Equal(new DateTimeOffset(wy, wmo, wd, 11, 0, 0, TimeSpan.Zero), MissionClock.LastWeekly(_settings, now));
        Assert.Equal(MissionClock.LastDaily(_settings, now).AddDays(1), MissionClock.NextDaily(_settings, now));
    }

    [Fact]
    public void TheResetMinuteAndDayAreSettings()
    {
        var settings = new MissionSettings { ResetHourUtc = 3, ResetMinute = 30, WeeklyResetDay = DayOfWeek.Sunday };
        var now = new DateTimeOffset(2026, 10, 1, 3, 29, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 3, 30, 0, TimeSpan.Zero), MissionClock.LastDaily(settings, now));
        Assert.Equal(new DateTimeOffset(2026, 9, 27, 3, 30, 0, TimeSpan.Zero), MissionClock.LastWeekly(settings, now));
    }

    [SkippableFact]
    public async Task ThePlayersMissionsAreKeptAndAnsweredInTheFixedAnswersShape()
    {
        Skip.If(_mongo is null, "set OVS_TEST_MONGO to run");
        string id = ObjectId.GenerateNewId().ToString();
        var service = Service();
        var first = await service.GetOrCreateAsync(id, default);
        var again = await Service().GetOrCreateAsync(id, default);
        Assert.Equal(first.ToJsonString(), again.ToJsonString());

        var body = first["body"]!.AsObject();
        Assert.Equal(MissionObject.Answer("x", enabled: false)["body"]!.AsObject().Select(kv => kv.Key), body.Select(kv => kv.Key));
        Assert.Equal(id, body["owner_id"]!.GetValue<string>());
        Assert.NotEqual(id, body["id"]!.GetValue<string>());
        Assert.Equal(35, body["server_data"]!["MissionControllerContainers"]!.AsObject().Count);

        // A container dropped from the setting is not answered (and is kept).
        _settings.Containers = "miscon_battlepassweekly_s5";
        var only = await Service().GetOrCreateAsync(id, default);
        Assert.Equal(["miscon_battlepassweekly_s5"], only["body"]!["server_data"]!["MissionControllerContainers"]!.AsObject().Select(kv => kv.Key));
        _settings.Containers = new MissionSettings().Containers;
        Assert.Equal(35, (await Service().GetOrCreateAsync(id, default))["body"]!["server_data"]!["MissionControllerContainers"]!.AsObject().Count);

        // After the daily reset the store has the new dailies.
        _time.Now = _time.Now.AddDays(1);
        var next = await Service().GetOrCreateAsync(id, default);
        Assert.Equal(2, next["body"]!["server_data"]!["MissionControllerContainers"]!["miscon_battlepassdaily_s5"]!["MissionControllers"]!["misctl_battlepass_daily_new_1"]!["Missions"]!.AsArray().Count);
        Assert.Equal(next.ToJsonString(), (await Service().GetOrCreateAsync(id, default)).ToJsonString());
    }

    [SkippableFact]
    public async Task ALostRaceAnswersWhatTheOtherRequestStored()
    {
        Skip.If(_mongo is null, "set OVS_TEST_MONGO to run");
        string id = ObjectId.GenerateNewId().ToString();
        // Two requests at once for a new player: both roll, one stores; both answer the stored missions.
        var answers = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Service(new MissionRandom()).GetOrCreateAsync(id, default)));
        var stored = await Service().GetOrCreateAsync(id, default);
        Assert.All(answers, a => Assert.Equal(stored.ToJsonString(), a.ToJsonString()));
    }
}

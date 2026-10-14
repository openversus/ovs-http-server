using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenVersus.Server.Core.Hiss;
using OpenVersus.Server.Core.Arenas;
using OpenVersus.Server.Core.Clients;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Cosmetics;
using OpenVersus.Server.Core.Identity;
using OpenVersus.Server.Core.Leaderboards;
using OpenVersus.Server.Core.Lobbies;
using OpenVersus.Server.Core.Matches;
using OpenVersus.Server.Core.Matchmaking;
using OpenVersus.Server.Core.Realtime;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Tests.Matches;

/// <summary>
/// The matchmaking request and its cancel (tools/matches/queue_diff.mjs compares them with the TS server's). Real Redis,
/// database 15 (OVS_TEST_REDIS); no Mongo, so every skill is 0 (the ratings: EloRatings).
/// </summary>
[Collection(RedisTestDatabase.Name)]
public sealed class MatchmakingRequestTests : IAsyncLifetime
{
    private static readonly string? s_redis = Environment.GetEnvironmentVariable("OVS_TEST_REDIS");
    private const string Me = "0000000000000000000b0001";
    private const string Mate = "0000000000000000000b0002";
    // An Arena lobby's third and fourth players.
    private const string Third = "0000000000000000000b0003", Fourth = "0000000000000000000b0004";
    private const string Lobby = "0000000000000000000b0100";
    private const string Ip = "198.51.100.31";

    private ConnectionMultiplexer? _redis;
    private readonly Gate _gate = new();
    // The tickets QueuedAsync has handed out already.
    private readonly HashSet<string> _taken = [];

    private sealed class Gate : IClientUpdateGate
    {
        public HashSet<string> Outdated { get; } = [];
        public List<string> Modals { get; } = [];

        public Task<IReadOnlyList<ClientUpdateState>> RequiringUpdateAsync(IEnumerable<string> playerIds) =>
            Task.FromResult<IReadOnlyList<ClientUpdateState>>(playerIds.Where(Outdated.Contains).Select(p => new ClientUpdateState(p, "", false, true)).ToList());

        public Task<IReadOnlyList<bool>> RequestModalsAsync(IEnumerable<string> playerIds)
        {
            Modals.AddRange(playerIds);
            return Task.FromResult<IReadOnlyList<bool>>([]);
        }

        public Task<ClientUpdateState> ForRequestAsync(AccountLookup lookup, JsonObject? claims) => throw new NotSupportedException();
        public Task<double> ModalNonceAsync(string playerId) => throw new NotSupportedException();
        public JsonObject FailureBody() => new() { ["body"] = new JsonObject { ["error"] = "client_update_required" } };
    }

    private sealed class Cosmetics : ICosmeticsService
    {
        public List<string> Copied { get; } = [];
        public Task<JsonObject> EquippedAsync(string accountId, CancellationToken ct) => Task.FromResult(new JsonObject { ["Banner"] = "banner_x" });
        public Task WriteMatchCopyAsync(string accountId, JsonObject cosmetics)
        {
            Copied.Add(accountId);
            return Task.CompletedTask;
        }

        public Task<bool> EquipTauntAsync(string accountId, JsonNode? character, JsonNode? index, JsonNode? slug, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> EquipStatTrackerAsync(string accountId, JsonNode? index, JsonNode? slug, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> EquipAsync(CosmeticSlot slot, string accountId, JsonNode? slug, bool given, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> SetProfileIconAsync(string accountId, JsonNode? slug, CancellationToken ct) => throw new NotSupportedException();
    }

    private readonly Cosmetics _cosmetics = new();

    private sealed class Launcher : IMatchLauncher
    {
        public MatchLaunch? Launched { get; private set; }

        public Task<LaunchedMatch?> LaunchAsync(MatchLaunch launch, CancellationToken ct)
        {
            Launched = launch;
            return Task.FromResult<LaunchedMatch?>(new LaunchedMatch("0000000000000000000b0999", 57000));
        }

        public Task<int?> RollbackPortAsync(IDatabase redis) => Task.FromResult<int?>(57000);

        public void DeployIfOnDemand(int port, string matchId)
        {
        }
    }

    private readonly Launcher _launcher = new();

    public async Task InitializeAsync()
    {
        if (string.IsNullOrEmpty(s_redis))
        {
            return;
        }

        string[] parts = s_redis.Split(':');
        var options = new ConfigurationOptions { EndPoints = { { parts[0], parts.Length > 1 ? int.Parse(parts[1]) : 6379 } }, DefaultDatabase = 15 };
        options.User = Environment.GetEnvironmentVariable("OVS_TEST_REDIS_USER");
        options.Password = Environment.GetEnvironmentVariable("OVS_TEST_REDIS_PW");
        _redis = await ConnectionMultiplexer.ConnectAsync(options);
        await CleanAsync();
    }

    public async Task DisposeAsync()
    {
        if (_redis is not null)
        {
            await CleanAsync();
            await _redis.DisposeAsync();
        }
    }

    private async Task CleanAsync()
    {
        var server = _redis!.GetServer(_redis.GetEndPoints()[0]);
        foreach (var key in server.Keys(15, "*0000000000000000000b*"))
        {
            await Db.KeyDeleteAsync(key);
        }

        await Db.KeyDeleteAsync(["1v1", "2v2", MatchmakingWorker.Casual1v1, MatchmakingWorker.Casual2v2, MatchmakingWorker.Ffa, ArenaMatchmaking.List, $"connections:{Ip}"]);
        await Db.HashDeleteAsync(MatchmakingQueue.QueuedKey, [Me, Mate, Third, Fourth]);
        await Db.SortedSetRemoveAsync(ArenaRunStore.DeadlinesKey, Lobby);
    }

    private IDatabase Db => _redis!.GetDatabase();

    // FFA always open unless a test says otherwise (FfaScheduleTests has the window).
    private MatchmakingRequestService Service(MongoDB.Driver.IMongoDatabase? mongo = null, FfaSettings? ffa = null, TimeProvider? time = null, bool withRedis = true,
        TestingGroundsSettings? testingGrounds = null, BetaSpeedSettings? betaSpeed = null)
    {
        var collection = new ServiceCollection();
        if (testingGrounds is not null)
        {
            collection.AddSingleton<IOptionsMonitor<TestingGroundsSettings>>(new TestOptions<TestingGroundsSettings>(testingGrounds));
        }

        if (betaSpeed is not null)
        {
            collection.AddSingleton<IOptionsMonitor<BetaSpeedSettings>>(new TestOptions<BetaSpeedSettings>(betaSpeed));
        }

        if (withRedis && _redis is not null)
        {
            collection.AddSingleton<IConnectionMultiplexer>(_redis);
        }

        if (mongo is not null)
        {
            collection.AddSingleton(mongo);
        }

        var services = collection.BuildServiceProvider();
        return new MatchmakingRequestService(services, _gate, _cosmetics,
            new EloRatings(services, new TestOptions<RankedSettings>(new RankedSettings()), TimeProvider.System, NullLogger<EloRatings>.Instance), _launcher,
            new TestOptions<LobbySettings>(new LobbySettings { GameVersion = "195303.1.1" }), new TestOptions<FfaSettings>(ffa ?? new FfaSettings { WeekendOnly = false }),
            time ?? TimeProvider.System, NullLogger<MatchmakingRequestService>.Instance);
    }

    // start_arena_direct's (the arenas service's), on the same Redis, gate and launcher.
    private ArenaService Arenas(TimeProvider? time = null) => new(new ServiceCollection().AddSingleton<IConnectionMultiplexer>(_redis!).BuildServiceProvider(),
        _gate, _launcher, new RandomArenaBotMatches(), time ?? TimeProvider.System, NullLogger<ArenaService>.Instance);

    // An Arena started for Me, and the config the match flow keeps for Me once its launch is announced (GameplayConfigs:
    // the match's id in its payload, every participant in its Players).
    private async Task<ArenaRun> ArenaStartedAsync()
    {
        await PlayerAsync(Me);
        await ArenaLobbyAsync(Me);
        await Arenas().StartAsync(Asking(Me) with { Body = new JsonObject { ["ArenaLobbyId"] = Lobby } }, CancellationToken.None);
        var run = (await ArenaRunStore.GetAsync(Db, Lobby))!;
        string match = run.Pairings.Single(p => p.HasPlayer).MatchId!;
        var players = new JsonObject();
        foreach (var participant in _launcher.Launched!.Players)
        {
            players[participant.PlayerId] = new JsonObject { ["Character"] = "character_lobby", ["Skin"] = "skin_lobby" };
        }

        await Db.StringSetAsync(GameplayConfigs.Key(Me), Js.Stringify(new JsonObject
        {
            ["data"] = new JsonObject { ["MatchId"] = match, ["GameplayConfig"] = new JsonObject { ["Players"] = players }, ["template_id"] = "OnGameplayConfigNotified" },
            ["payload"] = new JsonObject { ["match"] = new JsonObject { ["id"] = match } },
        }), TimeSpan.FromMinutes(20));
        return run;
    }

    private PartyRequest Arena(JsonObject body)
    {
        body["ArenaLobbyId"] = Lobby;
        return Asking(Me) with { Body = body };
    }

    private async Task<JsonObject> KeptConfigAsync() => (JsonObject)JsonNode.Parse((string)(await Db.StringGetAsync(GameplayConfigs.Key(Me)))!)!;

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static PartyRequest Asking(string player) => new(player, new JsonObject { ["id"] = player, ["profile_id"] = "0000000000000000000b0900" }, Ip,
        new JsonObject { ["data"] = new JsonObject { ["MultiplayParams"] = new JsonObject { ["MultiplayClusterSlug"] = "ec2-us-east-1-dokken" } }, ["match"] = "0000000000000000000b0700" });

    private async Task PlayerAsync(string id, string character = "character_jason", bool ip = true)
    {
        HashEntry[] loadout = [new("character", character), new("skin", "skin_jason_default"), .. ip ? new HashEntry[] { new("ip", Ip) } : []];
        await Db.HashSetAsync($"player:{id}", loadout);
        await Db.HashSetAsync($"connections:{id}", [new("id", id), new("current_ip", Ip), new("character", "character_shaggy")]);
        await Db.StringSetAsync($"player:{id}:cosmetics", "{}");
    }

    private async Task LobbyAsync(params string[] players)
    {
        await Db.StringSetAsync($"lobby:{Lobby}", Js.Stringify(new JsonObject { ["lobbyId"] = Lobby, ["playerIds"] = new JsonArray([.. players.Select(p => (JsonNode?)p)]) }));
        foreach (string p in players)
        {
            await Db.StringSetAsync($"player_lobby:{p}", Lobby);
        }
    }

    private static readonly string[] s_lists = ["1v1", "2v2", MatchmakingWorker.Casual1v1, MatchmakingWorker.Casual2v2, ArenaMatchmaking.List];

    // An Arena lobby led by <paramref name="players"/>[0], as ArenaLobbyService stores one.
    private async Task ArenaLobbyAsync(params string[] players)
    {
        await Db.StringSetAsync($"lobby:{Lobby}", Js.Stringify(new JsonObject
        {
            ["lobbyId"] = Lobby, ["ownerId"] = players[0], ["ownerUsername"] = "Leader", ["mode"] = ArenaLobby.TemplateName,
            ["playerIds"] = new JsonArray([.. players.Select(p => (JsonNode?)p)]), ["createdAt"] = 0,
        }));
        foreach (string p in players)
        {
            await Db.StringSetAsync($"player_lobby:{p}", Lobby);
        }
    }

    // This class's tickets in the queues' lists.
    private async Task<List<string>> TicketsAsync()
    {
        var tickets = new List<string>();
        foreach (string list in s_lists)
        {
            tickets.AddRange((await Db.ListRangeAsync(list)).Select(v => v.ToString()).Where(t => t.Contains("0000000000000000000b", StringComparison.Ordinal)));
        }

        return tickets;
    }

    // The next ticket queued (MatchmakingQueue), not handed out before.
    private async Task<string> QueuedAsync()
    {
        for (int i = 0; i < 50; i++)
        {
            if ((await TicketsAsync()).FirstOrDefault(t => !_taken.Contains(t)) is { } ticket)
            {
                _taken.Add(ticket);
                return ticket;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("nothing queued");
    }

    [SkippableFact]
    // queueMatch's ticket, key for key and in order (the matchmaker removes it by its bytes); no ip when player:{id} has none.
    public async Task TheTicketIsTheTsServersBytes()
    {
        Skip.If(_redis is null, "set OVS_TEST_REDIS to run");
        await PlayerAsync(Me);
        await PlayerAsync(Mate, ip: false);

        string ticket = await Service().TicketAsync(Db, Me, [Me, Mate], JsonValue.Create("0000000000000000000b0700"), "0000000000000000000b0800", "2v2",
            DateTimeOffset.FromUnixTimeSeconds(1790000000), CancellationToken.None);

        Assert.Equal(
            """{"created_at":1790000000,"matchType":"2v2","partyLeaderId":"0000000000000000000b0001","matchmakingRequestId":"0000000000000000000b0800","partyId":"0000000000000000000b0700","party_size":2,"players":[{"id":"0000000000000000000b0001","region":"MVSI","skill":0,"ip":"198.51.100.31"},{"id":"0000000000000000000b0002","region":"MVSI","skill":0}]}""",
            ticket);
    }

    [SkippableFact]
    public async Task ASoloRequestAnswersThenQueuesAndCleansUpFirst()
    {
        Skip.If(_redis is null, "set OVS_TEST_REDIS to run");
        await PlayerAsync(Me);
        await Db.ListRightPushAsync("1v1", """{"players":[{"id":"0000000000000000000b0001"}]}""");
        await Db.ListRightPushAsync("1v1", """{"players":[{"id":"someone_else"}]}""");
        await Db.StringSetAsync($"player_ranked_set:{Me}", "0000000000000000000b0200");
        await Db.StringSetAsync("ranked_set:0000000000000000000b0200", """{"players":[{"playerId":"0000000000000000000b0001"},{"playerId":"0000000000000000000b0003"}]}""");
        await Db.StringSetAsync("player_ranked_set:0000000000000000000b0003", "0000000000000000000b0200");

        var answer = (await Service().RequestAsync("ranked-1v1-retail", Asking(Me), CancellationToken.None))!;

        Assert.Equal(200, answer.Status);
        var body = answer.Body.AsObject();
        Assert.Equal("1v1-retail", (string?)body["criteria_slug"]);
        Assert.Equal("id", body.Last().Key);
        Assert.Equal("0000000000000000000b0900", (string?)body["players"]![Me]!["id"]);
        Assert.Null(body["party_id"]);
        // Nothing queued until the answer has been sent.
        Assert.Empty(await TicketsAsync());
        Assert.Equal(["""{"players":[{"id":"someone_else"}]}"""], (await Db.ListRangeAsync("1v1")).Select(v => v.ToString()));
        Assert.False(await Db.KeyExistsAsync("ranked_set:0000000000000000000b0200"));
        Assert.False(await Db.KeyExistsAsync("player_ranked_set:0000000000000000000b0003"));
        Assert.Equal("character_jason", (string?)await Db.HashGetAsync($"connections:{Me}", "character"));
        Assert.Empty(_cosmetics.Copied);

        await answer.After!();
        var ticket = Js.Parse(await QueuedAsync())!;
        Assert.Equal("1v1", (string?)ticket["matchType"]);
        Assert.Equal((string?)body["id"], (string?)ticket["matchmakingRequestId"]);
        Assert.Equal("0000000000000000000b0700", (string?)ticket["partyId"]);
    }

    [SkippableFact]
    public async Task A1v1RequestFromALobbyOfTwoIsThe2v2Request()
    {
        Skip.If(_redis is null, "set OVS_TEST_REDIS to run");
        await PlayerAsync(Me);
        await PlayerAsync(Mate, character: "character_taz");
        await LobbyAsync(Me, Mate);

        var answer = (await Service().RequestAsync("1v1-retail", Asking(Me), CancellationToken.None))!;

        var body = answer.Body.AsObject();
        Assert.Equal("2v2-retail", (string?)body["criteria_slug"]);
        Assert.Equal("id", body.First().Key);
        Assert.Equal(Lobby, (string?)body["party_id"]);
        Assert.Equal(2, (int)body["data"]!["player_count"]!);
        // The teammate's loadout becomes their session's too.
        Assert.Equal("character_taz", (string?)await Db.HashGetAsync($"connections:{Mate}", "character"));
        await answer.After!();
        var ticket = Js.Parse(await QueuedAsync())!;
        Assert.Equal([Me, Mate], ticket["players"]!.AsArray().Select(p => (string?)p!["id"]));
    }

    [SkippableFact]
    public async Task A2v2WithADisconnectedTeammateIsRefused()
    {
        Skip.If(_redis is null, "set OVS_TEST_REDIS to run");
        await PlayerAsync(Me);
        await LobbyAsync(Me, Mate);

        var answer = (await Service().RequestAsync("2v2-retail", Asking(Me), CancellationToken.None))!;

        Assert.Equal((200, """{"error":"Not all party members are connected"}"""), (answer.Status, Js.Stringify(answer.Body)));
        Assert.Null(answer.After);
    }

    [SkippableFact]
    public async Task NoLockedLoadoutIs500AndAPlayerWhoNeverEquippedGetsTheirCosmeticsCached()
    {
        Skip.If(_redis is null, "set OVS_TEST_REDIS to run");
        await Db.HashSetAsync($"connections:{Me}", [new("id", Me)]);

        var answer = (await Service().RequestAsync("1v1-retail", Asking(Me), CancellationToken.None))!;

        Assert.Equal((500, """{"error":"player_loadout_not_found"}"""), (answer.Status, Js.Stringify(answer.Body)));
        Assert.Equal([Me], _cosmetics.Copied);
    }

    [SkippableFact]
    public async Task AnOutdatedTeammateBlocksThe2v2WithTheModal()
    {
        Skip.If(_redis is null, "set OVS_TEST_REDIS to run");
        await PlayerAsync(Me);
        await PlayerAsync(Mate);
        await LobbyAsync(Me, Mate);
        _gate.Outdated.Add(Mate);

        var answer = (await Service().RequestAsync("2v2-retail", Asking(Me), CancellationToken.None))!;

        Assert.Equal("client_update_required", (string?)answer.Body["body"]!["error"]);
        Assert.Equal([Mate], _gate.Modals);
    }

    [SkippableFact]
    public async Task AnOtherCriteriaIsNotAnswered()
    {
        Skip.If(_redis is null, "set OVS_TEST_REDIS to run");
        Assert.Null(await Service().RequestAsync("ranked-2v2-retail", Asking(Me), CancellationToken.None));
    }

    [SkippableFact]
    // Everyone in the lobby is canceled (MatchmakingQueue: their ticket out of its list), and the lobby unreadied.
    public async Task ACancelReachesTheWholeLobbyAndUnreadiesIt()
    {
        Skip.If(_redis is null, "set OVS_TEST_REDIS to run");
        await LobbyAsync(Me, Mate);
        await Db.StringSetAsync($"party_ready:{Lobby}", "1");
        string ticket = """{"matchType":"2v2","players":[{"id":"0000000000000000000b0001"},{"id":"0000000000000000000b0002"}],"matchmakingRequestId":"0000000000000000000b0800"}""";
        await Db.ListRightPushAsync("2v2", ticket);
        await Db.HashSetAsync(MatchmakingQueue.QueuedKey, [new(Me, ticket), new(Mate, ticket)]);

        var answer = await Service().CancelAsync("0000000000000000000b0800", Asking(Me), CancellationToken.None);

        Assert.Equal("""{"body":{},"metadata":null,"return_code":0}""", Js.Stringify(answer));
        Assert.Empty(await TicketsAsync());
        Assert.False(await Db.HashExistsAsync(MatchmakingQueue.QueuedKey, Me));
        Assert.False(await Db.HashExistsAsync(MatchmakingQueue.QueuedKey, Mate));
        Assert.False(await Db.KeyExistsAsync($"party_ready:{Lobby}"));
    }

    [SkippableFact]
    // Casual: alone, the casual1v1 list; skill 0 and the regular ratings never read (Mongo is not even configured here).
    public async Task ACasualRequestAloneIsACasual1v1Ticket()
    {
        Skip.If(_redis is null, "set OVS_TEST_REDIS to run");
        await PlayerAsync(Me);
        await Db.ListRightPushAsync(MatchmakingWorker.Casual1v1, """{"players":[{"id":"0000000000000000000b0001"}]}""");

        var answer = (await Service().RequestAsync("casual-retail", Asking(Me), CancellationToken.None))!;

        Assert.Equal("casual-retail", (string?)answer.Body["criteria_slug"]);
        // The stale Casual ticket went when the player queued again.
        Assert.Empty(await Db.ListRangeAsync(MatchmakingWorker.Casual1v1));
        await answer.After!();
        var ticket = Js.Parse(await QueuedAsync())!;
        Assert.Equal((MatchmakingWorker.Casual1v1, 0), ((string?)ticket["matchType"], (int)ticket["players"]![0]!["skill"]!));
    }

    [SkippableFact]
    public async Task ACasualRequestFromALobbyOfTwoIsACasual2v2Ticket()
    {
        Skip.If(_redis is null, "set OVS_TEST_REDIS to run");
        await PlayerAsync(Me);
        await PlayerAsync(Mate);
        await LobbyAsync(Me, Mate);

        var answer = (await Service().RequestAsync("casual-retail", Asking(Me), CancellationToken.None))!;

        Assert.Equal(("casual-retail", Lobby), ((string?)answer.Body["criteria_slug"], (string?)answer.Body["party_id"]));
        await answer.After!();
        Assert.Equal(MatchmakingWorker.Casual2v2, (string?)Js.Parse(await QueuedAsync())!["matchType"]);
    }

    [SkippableFact]
    // A Casual ticket of theirs that no queue pointer names goes too.
    public async Task ACancelTakesTheLobbysCasualTicketsOut()
    {
        Skip.If(_redis is null, "set OVS_TEST_REDIS to run");
        await LobbyAsync(Me, Mate);
        await Db.ListRightPushAsync(MatchmakingWorker.Casual2v2, """{"players":[{"id":"0000000000000000000b0001"},{"id":"0000000000000000000b0002"}]}""");
        await Db.ListRightPushAsync(MatchmakingWorker.Casual2v2, """{"players":[{"id":"someone_else"}]}""");

        await Service().CancelAsync("0000000000000000000b0800", Asking(Mate), CancellationToken.None);

        Assert.Equal(["""{"players":[{"id":"someone_else"}]}"""], (await Db.ListRangeAsync(MatchmakingWorker.Casual2v2)).Select(v => v.ToString()));
    }

    [SkippableFact]
    // The regular ratings are for the regular queues only: a Casual ticket never reads them (nor makes one). The control:
    // the same rating is the skill of a regular request.
    public async Task CasualNeverTouchesTheRegularRatings()
    {
        string? mongoUri = Environment.GetEnvironmentVariable("OVS_TEST_MONGO");
        Skip.If(_redis is null || string.IsNullOrEmpty(mongoUri), "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        var client = new MongoDB.Driver.MongoClient(mongoUri);
        const string database = "ovs_matchmaking_request_tests";
        await client.DropDatabaseAsync(database);
        var mongo = client.GetDatabase(database);
        var ratings = mongo.GetCollection<MongoDB.Bson.BsonDocument>("eloratings");
        try
        {
            await PlayerAsync(Me);
            await PlayerAsync(Mate);
            // The character rated in both modes, so a Casual ticket would show either field it read.
            var jason = new MongoDB.Bson.BsonDocument("character_jason", new MongoDB.Bson.BsonDocument("elo", 1500));
            await ratings.InsertOneAsync(new MongoDB.Bson.BsonDocument { ["account_id"] = Me, ["elo_1v1"] = 1000, ["characters_1v1"] = jason, ["characters_2v2"] = jason.DeepClone() });

            // Mate has no rating at all: a Casual ticket must not make one.
            foreach (string player in new[] { Me, Mate })
            {
                var casual = (await Service(mongo).RequestAsync("casual-retail", Asking(player), CancellationToken.None))!;
                await casual.After!();
                var casualTicket = Js.Parse(await QueuedAsync())!;
                Assert.Equal(0, (int)casualTicket["players"]![0]!["skill"]!);
            }

            Assert.Equal(1, await ratings.CountDocumentsAsync(MongoDB.Driver.FilterDefinition<MongoDB.Bson.BsonDocument>.Empty));

            var regular = (await Service(mongo).RequestAsync("1v1-retail", Asking(Me), CancellationToken.None))!;
            await regular.After!();
            var regularTicket = Js.Parse(await QueuedAsync())!;
            Assert.Equal(1500, (double)regularTicket["players"]![0]!["skill"]!);
            Assert.Equal(1, await ratings.CountDocumentsAsync(MongoDB.Driver.FilterDefinition<MongoDB.Bson.BsonDocument>.Empty));
        }
        finally
        {
            await client.DropDatabaseAsync(database);
        }
    }

    [SkippableFact]
    // Alone: a 1v1 against one bot at Medium, a roster fighter in one of its skins, the default perks; the answer is the
    // TS catch-all the game takes as "match found".
    public async Task CasualBotsForAPlayerAloneIsA1v1AgainstOneBot()
    {
        Skip.If(_redis is null, "set OVS_TEST_REDIS to run");
        await PlayerAsync(Me);
        await Db.ListRightPushAsync(MatchmakingWorker.Casual1v1, """{"players":[{"id":"0000000000000000000b0001"}]}""");

        var answer = await Service().CasualBotsAsync(Asking(Me), CancellationToken.None);

        Assert.Equal((new HissSettings().MatchmakingCrc, 200), ((int)answer["body"]!["MatchmakingCrc"]!, (int)answer["return_code"]!));
        var launch = _launcher.Launched!;
        Assert.Equal(("1v1", "1v1"), (launch.Mode, launch.MatchType));
        // Unranked for the TS websocket (the unranked config, no best-of-3 set, never rated), not a custom lobby's match.
        Assert.Equal("""{"isCustomGame":true}""", Js.Stringify(launch.NotificationFields));
        Assert.Equal("""{"bIsCustomGame":false}""", Js.Stringify(launch.GameplayConfigOverride));
        Assert.Equal(BotDefaults.Perks, launch.BotPerks!.Select(p => (string)p!));
        var human = Assert.Single(launch.Players, p => !p.IsBot);
        var bot = Assert.Single(launch.Players, p => p.IsBot);
        Assert.Equal((Me, 0, 0, true, "198.51.100.31"), (human.PlayerId, human.PlayerIndex, human.TeamIndex, human.IsHost, human.Ip));
        Assert.Matches("^Bot[0-9A-F]{32}$", bot.PlayerId);
        Assert.Equal((1, 1, false), (bot.PlayerIndex, bot.TeamIndex, bot.IsHost));
        var config = (await Db.HashGetAllAsync($"bot_config:{bot.PlayerId}")).ToDictionary(e => e.Name.ToString(), e => e.Value.ToString());
        Assert.Contains(BotFighters.All, f => f.Character == config["character"] && f.Skins.Contains(config["skin"]));
        Assert.Equal(("2", "2", "Medium"), (config["difficultyMin"], config["difficultyMax"], config["settingSlug"]));
        Assert.True(await Db.KeyTimeToLiveAsync($"bot_config:{bot.PlayerId}") > TimeSpan.FromHours(23));
        Assert.Empty(await Db.ListRangeAsync(MatchmakingWorker.Casual1v1));
        Assert.Equal("character_jason", (string?)await Db.HashGetAsync($"connections:{Me}", "character"));
        await Db.KeyDeleteAsync($"bot_config:{bot.PlayerId}");
    }

    [SkippableFact]
    public async Task CasualBotsForAPartyIsA2v2AgainstTwoDifferentBots()
    {
        Skip.If(_redis is null, "set OVS_TEST_REDIS to run");
        await PlayerAsync(Me);
        await PlayerAsync(Mate);
        await LobbyAsync(Mate, Me);

        await Service().CasualBotsAsync(Asking(Me), CancellationToken.None);

        var launch = _launcher.Launched!;
        Assert.Equal("2v2", launch.Mode);
        Assert.Equal([(Me, 0, 0, true), (Mate, 2, 0, false)], launch.Players.Where(p => !p.IsBot).Select(p => (p.PlayerId, p.PlayerIndex, p.TeamIndex, p.IsHost)));
        var bots = launch.Players.Where(p => p.IsBot).ToList();
        Assert.Equal([(1, 1), (3, 1)], bots.Select(b => (b.PlayerIndex, b.TeamIndex)));
        var characters = new List<string>();
        foreach (var bot in bots)
        {
            characters.Add((string)(await Db.HashGetAsync($"bot_config:{bot.PlayerId}", "character"))!);
            await Db.KeyDeleteAsync($"bot_config:{bot.PlayerId}");
        }

        Assert.Equal(2, characters.Distinct().Count());
    }

    [SkippableFact]
    public async Task CasualBotsWaitForEveryClientToBeCurrent()
    {
        Skip.If(_redis is null, "set OVS_TEST_REDIS to run");
        await PlayerAsync(Me);
        _gate.Outdated.Add(Me);

        var answer = await Service().CasualBotsAsync(Asking(Me), CancellationToken.None);

        Assert.Equal("client_update_required", (string?)answer["body"]!["error"]);
        Assert.Null(_launcher.Launched);
        Assert.Equal([Me], _gate.Modals);
    }

    [SkippableFact]
    // FFA alone: the 1v1 request answered with criteria ffa, onto the FFA list with skill 0 (MatchmakingQueue); a ticket of the player's in
    // another queue goes first, and a stale FFA ticket goes when they ask for another queue.
    public async Task AnFfaRequestAloneIsAnFfaTicket()
    {
        Skip.If(_redis is null, "set OVS_TEST_REDIS to run");
        await PlayerAsync(Me);
        await Db.ListRightPushAsync("2v2", """{"players":[{"id":"0000000000000000000b0001"}]}""");

        var answer = (await Service().RequestAsync("ffa", Asking(Me) with { BetaSpeed = "110" }, CancellationToken.None))!;

        Assert.Equal((200, "ffa", 1), (answer.Status, (string?)answer.Body["criteria_slug"], (int)answer.Body["data"]!["player_count"]!));
        Assert.Empty(await Db.ListRangeAsync("2v2"));
        await answer.After!();
        // MatchmakingQueue pushes the ticket onto the list its matchType names.
        var ticket = Js.Parse((string?)await Db.ListGetByIndexAsync(MatchmakingWorker.Ffa, 0) ?? "null")!;
        Assert.Equal((MatchmakingWorker.Ffa, 0), ((string?)ticket["matchType"], (int)ticket["players"]![0]!["skill"]!));

        await Db.ListRightPushAsync(MatchmakingWorker.Ffa, """{"players":[{"id":"0000000000000000000b0001"}]}""");
        await Service().RequestAsync("1v1-retail", Asking(Me), CancellationToken.None);
        Assert.Empty(await Db.ListRangeAsync(MatchmakingWorker.Ffa));
    }

    [SkippableFact]
    // FFA is solo entry: a lobby of two is answered as queued (a refused request would leave the game waiting, its cancel
    // greyed out), nothing is queued, and both games are told the search started and then that it was cancelled, each
    // player gets a banner (no toast), and the party's ready goes.
    public async Task AnFfaRequestFromALobbyOfTwoIsAnsweredThenCancelled()
    {
        Skip.If(_redis is null, "set OVS_TEST_REDIS to run");
        await PlayerAsync(Me);
        await PlayerAsync(Mate);
        await LobbyAsync(Me, Mate);
        await Db.SetAddAsync($"party_ready:{Lobby}", [Me, Mate]);
        foreach (string player in new[] { Me, Mate })
        {
            await Db.StringSetAsync(GatewayPresence.ConnectionKey(player), "node");
        }

        var heard = new System.Collections.Concurrent.ConcurrentQueue<JsonObject>();
        await _redis!.GetSubscriber().SubscribeAsync(RedisChannel.Literal("ws:send"), (_, message) =>
        {
            if (JsonNode.Parse(message.ToString()) is JsonObject sent && sent["playerIds"]!.ToJsonString().Contains("0000000000000000000b"))
            {
                heard.Enqueue(sent);
            }
        });
        MatchmakingRequestService.FfaPartyCancelDelay = TimeSpan.Zero;

        var answer = (await Service().RequestAsync("ffa", Asking(Me) with { BetaSpeed = "110" }, CancellationToken.None))!;
        Assert.Equal(200, answer.Status);
        Assert.Equal("ffa", (string?)answer.Body["criteria_slug"]);
        string requestId = (string)answer.Body["id"]!;
        await answer.After!();

        for (int i = 0; i < 50 && heard.Count(h => (string?)h["message"]!["cmd"] == "matchmaking-cancel") < 1; i++)
        {
            await Task.Delay(50);
        }

        foreach (string player in new[] { Me, Mate })
        {
            var mine = heard.Where(h => h["playerIds"]!.ToJsonString().Contains(player)).Select(h => h["message"]!).ToList();
            Assert.Contains(mine, m => (string?)m["data"]?["template_id"] == "OnMatchmakerStarted" && (string?)m["data"]!["MatchmakingRequestId"] == requestId);
            Assert.Contains(mine, m => (string?)m["cmd"] == "matchmaking-cancel" && (string?)m["payload"]!["id"] == requestId);
            Assert.Contains("admin_banner", (await Db.ListRangeAsync($"{PlayerMessages.NotificationPrefix}{player}")).Select(v => v.ToString()).FirstOrDefault() ?? "");
        }

        Assert.Empty(await Db.ListRangeAsync(MatchmakingWorker.Ffa));
        Assert.False(await Db.HashExistsAsync(MatchmakingQueue.QueuedKey, Me));
        Assert.False(await Db.KeyExistsAsync($"party_ready:{Lobby}"));
        MatchmakingRequestService.FfaPartyCancelDelay = TimeSpan.FromSeconds(1.5);
    }

    [Fact]
    // Closed (a Tuesday in New York): the TS ffaQueueClosedFailure, 200, before anything else is read; nothing queued.
    // An outdated client is told to update first, as there. Needs no Redis: the service has none here.
    public async Task AnFfaRequestOutsideTheWindowIsRefused()
    {
        var tuesday = new Clock(DateTimeOffset.Parse("2026-10-06T18:00:00Z"));
        var service = Service(ffa: new FfaSettings(), time: tuesday, withRedis: false);

        var answer = (await service.RequestAsync("ffa", Asking(Me), CancellationToken.None))!;
        Assert.Equal((200, Js.Stringify(FfaSchedule.ClosedFailure())), (answer.Status, Js.Stringify(answer.Body)));
        Assert.Null(answer.After);

        _gate.Outdated.Add(Me);
        answer = (await service.RequestAsync("ffa", Asking(Me), CancellationToken.None))!;
        Assert.Equal("client_update_required", (string?)answer.Body["body"]!["error"]);
        Assert.Equal([Me], _gate.Modals);
    }

    [Theory]
    [InlineData("120", true)]
    [InlineData("110", false)]
    [InlineData(null, false)]
    // Testing Grounds runs Beta Speed: a game that reports another speed than BetaSpeed:Percent (it read the speed before
    // a change), or none (an older client), is told to restart. Needs no Redis: a refusal reads nothing, and an accepted
    // request goes on to the queue, the first thing that needs it.
    public async Task ATestingGroundsRequestMustRunTheServersBetaSpeed(string? reported, bool accepted)
    {
        var service = Service(withRedis: false, testingGrounds: new TestingGroundsSettings { AlwaysOpen = true }, betaSpeed: new BetaSpeedSettings { Percent = 120 });
        var request = Asking(Me) with { BetaSpeed = reported };

        if (accepted)
        {
            var past = await Assert.ThrowsAsync<InvalidOperationException>(() => service.RequestAsync(TestingGrounds.Criteria, request, CancellationToken.None));
            Assert.Contains("no Redis", past.Message);
            return;
        }

        var answer = (await service.RequestAsync(TestingGrounds.Criteria, request, CancellationToken.None))!;
        Assert.Equal((200, Js.Stringify(TestingGrounds.BetaSpeedFailure())), (answer.Status, Js.Stringify(answer.Body)));
        Assert.Null(answer.After);
    }

    [Theory]
    [InlineData("120", true)]
    [InlineData("110", false)]
    [InlineData(null, false)]
    // FFA runs Beta Speed too (Ffa:Mutators, by default): the same restart message for a game on another speed. With no
    // mutators any game may queue.
    public async Task AnFfaRequestMustRunTheServersBetaSpeedWhileFfaRunsIt(string? reported, bool accepted)
    {
        foreach (var settings in new[] { new FfaSettings { WeekendOnly = false }, new FfaSettings { WeekendOnly = false, Mutators = "" } })
        {
            var service = Service(withRedis: false, ffa: settings, betaSpeed: new BetaSpeedSettings { Percent = 120 });
            var request = Asking(Me) with { BetaSpeed = reported };
            if (accepted || settings.Mutators == "")
            {
                var past = await Assert.ThrowsAsync<InvalidOperationException>(() => service.RequestAsync("ffa", request, CancellationToken.None));
                Assert.Contains("no Redis", past.Message);
                continue;
            }

            var answer = (await service.RequestAsync("ffa", request, CancellationToken.None))!;
            Assert.Equal((200, Js.Stringify(TestingGrounds.BetaSpeedFailure())), (answer.Status, Js.Stringify(answer.Body)));
        }
    }

    [SkippableTheory]
    [InlineData("arena-retail")]
    [InlineData("arena")]
    // The Arena lobby's Start Matchmaking (its leader's): the whole lobby, every player, on one ticket on the arena list,
    // which no worker matches; the game's own timer cancels it and starts the Arena with bots.
    public async Task AnArenaLeadersRequestQueuesTheWholeLobbyAsOneTicket(string criteria)
    {
        Skip.If(_redis is null, "set OVS_TEST_REDIS to run");
        foreach (string p in (string[])[Me, Mate, Third, Fourth])
        {
            await PlayerAsync(p);
        }

        await ArenaLobbyAsync(Me, Mate, Third, Fourth);

        var answer = (await Service().RequestAsync(criteria, Asking(Me), CancellationToken.None))!;

        Assert.Equal(("arena-retail", Lobby, 4), ((string?)answer.Body["criteria_slug"], (string?)answer.Body["party_id"], (int)answer.Body["data"]!["player_count"]!));
        Assert.Equal([Me, Mate, Third, Fourth], answer.Body["players"]!.AsObject().Select(e => e.Key));
        await answer.After!();
        var ticket = Js.Parse(await QueuedAsync())!;
        Assert.Equal((ArenaMatchmaking.List, Me), ((string?)ticket["matchType"], (string?)ticket["partyLeaderId"]));
        Assert.Equal([Me, Mate, Third, Fourth], ticket["players"]!.AsArray().Select(p => (string)p!["id"]!));
        Assert.Single(await TicketsAsync());
    }

    [SkippableFact]
    // start_arena_direct (the game's Arena timer ran out): the Arena starts with all 16 places and its run is kept (the
    // character select open); its player's team (them and a bot) plays its round-1 2v2 against a bot team, launched now
    // under the pairing's match id, with the player's own Arena data; a second start starts nothing.
    public async Task AnArenaStartsWithSixteenAndPlaysItsPlayersTeam()
    {
        Skip.If(_redis is null, "set OVS_TEST_REDIS to run");
        await PlayerAsync(Me);
        await ArenaLobbyAsync(Me);
        var asking = Asking(Me) with { Body = new JsonObject { ["ArenaLobbyId"] = Lobby, ["MatchId"] = Lobby, ["Mode"] = "" } };

        var answer = await Arenas().StartAsync(asking, CancellationToken.None);

        Assert.NotNull(answer["body"]!["Crc"]);
        var arena = Js.Parse((string)(await Db.StringGetAsync($"lobby:{Lobby}"))!)!["arena"]!;
        var teams = arena["teams"]!.AsArray().Select(t => t!.AsArray().Select(p => (string)p!).ToList()).ToList();
        Assert.Equal(16, teams.SelectMany(t => t).Distinct().Count());
        Assert.All(teams, t => Assert.Equal(2, t.Count));
        Assert.Equal(Me, teams[0][0]);
        var launch = _launcher.Launched!;
        Assert.Equal("2v2", launch.Mode);
        Assert.Equal([(Me, 0, 0, true)], launch.Players.Where(p => !p.IsBot).Select(p => (p.PlayerId, p.PlayerIndex, p.TeamIndex, p.IsHost)));
        Assert.Equal([(1, 1), (2, 0), (3, 1)], launch.Players.Where(p => p.IsBot).Select(p => (p.PlayerIndex, p.TeamIndex)).Order());
        Assert.Contains(launch.Players.Single(p => p.TeamIndex == 0 && p.IsBot).PlayerId, teams[0]);
        // An Arena round: ArenaModeInfo in the config (its presence makes the match an Arena), timed, no ring-out target;
        // the Arena beside the config, with the player's own entry.
        Assert.False((bool)launch.GameplayConfigOverride!["ArenaModeInfo"]!["bReadyToStart"]!);
        // No score target for either side: only the timer ends a round.
        Assert.Equal("[{\"TargetScore\":0},{\"TargetScore\":0}]", launch.GameplayConfigOverride["TeamData"]!.ToJsonString());
        Assert.Equal((90, 0), ((int)launch.NotificationFields!["customMatchTime"]!, (int)launch.NotificationFields["customNumRingouts"]!));
        Assert.Equal(16, launch.ConfigData!["ArenaData"]!["Players"]!.AsArray().Count);
        Assert.Equal(Me, (string?)launch.ConfigData["ArenaPlayerInfo"]!["AccountID"]);
        Assert.Equal(ArenaState.SelectableCount, launch.ConfigData["ArenaPlayerInfo"]!["SelectableCharacters"]!.AsArray().Count);
        var run = (await ArenaRunStore.GetAsync(Db, Lobby))!;
        Assert.Equal((ArenaPhase.CharacterSelect, 1), (run.Phase, run.Round));
        Assert.Equal(run.Pairings.Single(p => p.HasPlayer).MatchId, launch.MatchId);
        Assert.Equal([Me], launch.PlayerConfigData!.Keys);
        Assert.Equal(run.Players[Me].Pool, launch.PlayerConfigData[Me]["ArenaPlayerInfo"]!["SelectableCharacters"]!.AsArray().Select(c => (string)c!));
        Assert.All(run.Players.Values.Where(p => p.IsBot), b => Assert.NotNull(b.Character));
        var characters = new List<string>();
        foreach (string bot in teams.SelectMany(t => t).Where(p => p != Me))
        {
            characters.Add((string)(await Db.HashGetAsync($"bot_config:{bot}", "character"))!);
            await Db.KeyDeleteAsync($"bot_config:{bot}");
        }

        Assert.Equal(15, characters.Distinct().Count());

        await Arenas().StartAsync(asking, CancellationToken.None);
        Assert.Same(launch, _launcher.Launched);
        Assert.Equal(arena.ToJsonString(), Js.Parse((string)(await Db.StringGetAsync($"lobby:{Lobby}"))!)!["arena"]!.ToJsonString());
    }

    [SkippableFact]
    // Round 1 from the game's calls: a reroll answered (under Result) with the new offer and the coins after it; the pick
    // opens the shop, and the player's kept config is patched (their Arena data: no offer, the shop's pages; their match's
    // fighters as picked) and sent; the shop's summary starts the matches, the config sent again ready to start; a second
    // summary is refused.
    public async Task AnArenasRoundOneRunsFromTheGamesCalls()
    {
        Skip.If(_redis is null, "set OVS_TEST_REDIS to run");
        await ArenaStartedAsync();
        var arenas = Arenas();

        var reroll = (await arenas.AnswerAsync("arena_reroll_characters", Arena([]), CancellationToken.None)).Body;
        var run = (await ArenaRunStore.GetAsync(Db, Lobby))!;
        Assert.True((bool)reroll["Result"]!["BaseResponse"]!["bSuccess"]!);
        Assert.Equal(run.Players[Me].Pool, reroll["Result"]!["SelectableCharacters"]!.AsArray().Select(c => (string)c!));
        Assert.Equal(ArenaRules.Default.StartingCoins - ArenaRules.Default.CharacterRerollCost, (int)reroll["Result"]!["playerData"]!["CurrencyAmount"]!);

        string pick = run.Players[Me].Pool[0];
        var selected = (await arenas.AnswerAsync("arena_select_character", Arena(new JsonObject { ["CharacterSlug"] = pick, ["SkinSlug"] = "" }), CancellationToken.None)).Body;
        Assert.True((bool)selected["bSuccess"]!);
        run = (await ArenaRunStore.GetAsync(Db, Lobby))!;
        Assert.Equal(ArenaPhase.Shop, run.Phase);
        var kept = await KeptConfigAsync();
        var info = kept["data"]!["ArenaPlayerInfo"]!;
        Assert.Empty(info["SelectableCharacters"]!.AsArray());
        Assert.Equal(ArenaRules.Default.ShopPages, info["CurrentShopLocal"]!.AsArray().Count);
        Assert.Equal(pick, (string?)kept["data"]!["GameplayConfig"]!["Players"]![Me]!["Character"]);
        Assert.All(_launcher.Launched!.Players.Where(p => p.IsBot), b =>
            Assert.Equal(run.Players[b.PlayerId].Character, (string?)kept["data"]!["GameplayConfig"]!["Players"]![b.PlayerId]!["Character"]));
        var sent = await Db.StreamRangeAsync(PlayerMessages.LogKey(Me));
        Assert.Contains(sent, e => e.Values.Any(v => v.Value.ToString().Contains("\"CurrentShopLocal\"", StringComparison.Ordinal)));
        Assert.Equal(pick, (string?)await Db.HashGetAsync($"connections:{Me}", "character"));

        // A purchase the player can afford (their coins after the reroll), from whichever page offers one.
        run = (await ArenaRunStore.GetAsync(Db, Lobby))!;
        var (page, offer) = run.Players[Me].ShopPages.SelectMany((p, i) => p.Select((slug, j) => (i, j, slug)))
            .Where(o => ArenaItems.Of(o.slug)!.Cost <= run.Players[Me].Coins).Select(o => (o.i, o.j)).First();
        string bought = run.Players[Me].ShopPages[page][offer];
        var closed = new JsonObject { ["ArenaRound"] = 1, ["ShopPhaseDetails"] = new JsonObject { ["NumRerolls"] = 0, ["ItemTransactions"] = new JsonArray(
            new JsonObject { ["bPurchase"] = true, ["LocalShopIndex"] = page, ["ItemIndex"] = offer }) } };
        Assert.True((bool)(await arenas.AnswerAsync("arena_player_shop_closed", Arena(closed), CancellationToken.None)).Body["bSuccess"]!);
        Assert.Equal(ArenaPhase.Match, (await ArenaRunStore.GetAsync(Db, Lobby))!.Phase);
        var ready = await KeptConfigAsync();
        Assert.True((bool)ready["data"]!["GameplayConfig"]!["ArenaModeInfo"]!["bReadyToStart"]!);
        // No shop time left (a re-shown matchup screen's shop closes at once), and the bought item as the player's gem.
        Assert.Equal(0, (int)ready["data"]!["GameplayConfig"]!["ArenaModeInfo"]!["ShopTime"]!);
        Assert.Equal($"[{{\"Gem\":\"{ArenaItems.Of(bought)!.Gem}\",\"ChargeLevel\":0}}]", ready["data"]!["GameplayConfig"]!["Players"]![Me]!["Gems"]!.ToJsonString());
        Assert.All(_launcher.Launched!.Players.Where(p => p.IsBot), b => Assert.Empty(ready["data"]!["GameplayConfig"]!["Players"]![b.PlayerId]!["Gems"]!.AsArray()));
        // The round's config is launched under Attunements.Any, the only node attunement under which the game applies
        // Arena gems at spawn.
        Assert.Equal(ArenaState.GemAttunement, (string?)_launcher.Launched!.GameplayConfigOverride!["RiftNodeAttunement"]);
        // The pages stay: a re-activated matchup screen opens the shop again and reads its first page.
        Assert.Equal(ArenaRules.Default.ShopPages, ready["data"]!["ArenaPlayerInfo"]!["CurrentShopLocal"]!.AsArray().Count);
        Assert.Contains(await Db.StreamRangeAsync(PlayerMessages.LogKey(Me)), e => e.Values.Any(v => v.Value.ToString().Contains("\"bReadyToStart\":true", StringComparison.Ordinal)));
        Assert.False((bool)(await arenas.AnswerAsync("arena_player_shop_closed", Arena((JsonObject)closed.DeepClone()), CancellationToken.None)).Body["bSuccess"]!);
    }

    [SkippableFact]
    // Round 2 from the match's end: the round's match is named an Arena's before its launch; while it is still on, the
    // game's absent report is answered "keep waiting" (return_code 2) and counts as its check-in; the match's end (the
    // arena:results record's side, the player connected) is the round's last result, so round 2 starts at once: paired,
    // its match launched under its own id after the game is sent OnArenaNextMatch with it, its shop open. A report for
    // round 1 after that is plain success.
    public async Task AnArenasNextRoundStartsFromTheMatchsEnd()
    {
        Skip.If(_redis is null, "set OVS_TEST_REDIS to run");
        var run = await ArenaStartedAsync();
        var arenas = Arenas();
        await arenas.AnswerAsync("arena_select_character", Arena(new JsonObject { ["CharacterSlug"] = run.Players[Me].Pool[0] }), CancellationToken.None);
        await arenas.AnswerAsync("arena_player_shop_closed", Arena(new JsonObject { ["ArenaRound"] = 1, ["ShopPhaseDetails"] = new JsonObject() }), CancellationToken.None);
        string match = run.Pairings.Single(p => p.HasPlayer).MatchId!;
        Assert.Equal(Lobby, await ArenaResults.RunOfAsync(Db, match));
        var absent = Asking(Me) with { Body = new JsonObject { ["ArenaParentId"] = Lobby, ["ArenaRound"] = 1, ["ContainerMatchId"] = match } };

        var waiting = await arenas.AnswerAsync("arena_match_absent", absent, CancellationToken.None);
        Assert.Equal(ArenaAnswer.KeepWaiting, waiting.ReturnCode);
        Assert.True((bool)waiting.Body["bSuccess"]!);
        Assert.Equal((ArenaPhase.Match, 1), ((await ArenaRunStore.GetAsync(Db, Lobby))!.Phase, (await ArenaRunStore.GetAsync(Db, Lobby))!.Players[Me].CheckedInRound));

        await Db.SetAddAsync("online_players", Me);
        await arenas.MatchEndedAsync(Lobby, match, 0, CancellationToken.None);

        run = (await ArenaRunStore.GetAsync(Db, Lobby))!;
        Assert.Equal((ArenaPhase.Shop, 2, 1), (run.Phase, run.Round, run.Teams[0].Wins));
        string next = run.Pairings.Single(p => p.HasPlayer).MatchId!;
        Assert.NotEqual(match, next);
        Assert.Equal(next, _launcher.Launched!.MatchId);
        // The game's next check-in names the round its config says.
        Assert.Equal(2, (int)_launcher.Launched.PlayerConfigData![Me]["ArenaData"]!["CurrentRound"]!);
        Assert.Equal(Lobby, await ArenaResults.RunOfAsync(Db, next));
        var nextMatch = Assert.Single((await Db.StreamRangeAsync(PlayerMessages.LogKey(Me))).Select(e => e.Values.Select(v => v.Value.ToString()).FirstOrDefault(v => v.Contains("OnArenaNextMatch", StringComparison.Ordinal))).OfType<string>());
        Assert.Contains($"\"MatchId\":\"{next}\"", nextMatch, StringComparison.Ordinal);
        Assert.Equal(0, (await arenas.AnswerAsync("arena_match_absent", absent, CancellationToken.None)).ReturnCode);
    }

    [SkippableFact]
    // The end of a player's Arena (here their team out: their game gone, no report): they are sent ArenaPlayerEndPayload
    // with their Arena data beside its id, and an absent report from their game after that is answered with the same body
    // under return_code 13, which the game hands to the same handler.
    public async Task APlayerWhoseArenaEndedIsToldSo()
    {
        Skip.If(_redis is null, "set OVS_TEST_REDIS to run");
        var run = await ArenaStartedAsync();
        var arenas = Arenas();
        await arenas.AnswerAsync("arena_select_character", Arena(new JsonObject { ["CharacterSlug"] = run.Players[Me].Pool[0] }), CancellationToken.None);
        await arenas.AnswerAsync("arena_player_shop_closed", Arena(new JsonObject { ["ArenaRound"] = 1, ["ShopPhaseDetails"] = new JsonObject() }), CancellationToken.None);
        string match = run.Pairings.Single(p => p.HasPlayer).MatchId!;

        await arenas.MatchEndedAsync(Lobby, match, null, CancellationToken.None);

        run = (await ArenaRunStore.GetAsync(Db, Lobby))!;
        Assert.False(run.Teams[run.Players[Me].Team].In);
        string told = Assert.Single((await Db.StreamRangeAsync(PlayerMessages.LogKey(Me))).Select(e => e.Values.Select(v => v.Value.ToString())
            .FirstOrDefault(v => v.Contains("ArenaPlayerEndPayload", StringComparison.Ordinal))).OfType<string>());
        Assert.Contains($"\"ArenaId\":\"{Lobby}\"", told, StringComparison.Ordinal);
        Assert.Contains("\"ArenaData\":{", told, StringComparison.Ordinal);

        var absent = Asking(Me) with { Body = new JsonObject { ["ArenaParentId"] = Lobby, ["ArenaRound"] = 1, ["ContainerMatchId"] = match } };
        var answer = await arenas.AnswerAsync("arena_match_absent", absent, CancellationToken.None);
        Assert.Equal(ArenaAnswer.PlayerEnded, answer.ReturnCode);
        Assert.Equal("ArenaPlayerEndPayload", (string?)answer.Body["template_id"]);
        Assert.Equal(Lobby, (string?)answer.Body["ArenaId"]);
        Assert.Equal(run.Teams[run.Players[Me].Team].FinalRank,
            (int)answer.Body["ArenaData"]!["TeamInfo"]!.AsObject().Select(t => t.Value!).Single(t => t["Players"]!.AsArray().Any(p => (string?)p == Me))["FinalRank"]!);
    }

    [SkippableFact]
    // A kept config of another match (the last round's, before this round's launch is announced) is never patched, and
    // nothing is sent.
    public async Task AnArenaNeverPatchesAnotherMatchsConfig()
    {
        Skip.If(_redis is null, "set OVS_TEST_REDIS to run");
        var run = await ArenaStartedAsync();
        var other = await KeptConfigAsync();
        other["payload"]!["match"]!["id"] = "0000000000000000000b0999";
        await Db.StringSetAsync(GameplayConfigs.Key(Me), Js.Stringify(other));

        await Arenas().AnswerAsync("arena_select_character", Arena(new JsonObject { ["CharacterSlug"] = run.Players[Me].Pool[0] }), CancellationToken.None);

        Assert.Equal(ArenaPhase.Shop, (await ArenaRunStore.GetAsync(Db, Lobby))!.Phase);
        Assert.Equal(Js.Stringify(other), (string?)await Db.StringGetAsync(GameplayConfigs.Key(Me)));
        Assert.Empty(await Db.StreamRangeAsync(PlayerMessages.LogKey(Me)));
    }

    [SkippableFact]
    // The character select runs out with no pick: the deadline loop picks for the player (no bonus) and sends them the shop.
    public async Task AnArenasDeadlinePicksForAnAbsentPlayer()
    {
        Skip.If(_redis is null, "set OVS_TEST_REDIS to run");
        var started = await ArenaStartedAsync();

        await Arenas(new Clock(DateTimeOffset.FromUnixTimeMilliseconds(started.DeadlineMs - 1))).DeadlinesAsync(CancellationToken.None);
        Assert.Equal(ArenaPhase.CharacterSelect, (await ArenaRunStore.GetAsync(Db, Lobby))!.Phase);
        await Arenas(new Clock(DateTimeOffset.FromUnixTimeMilliseconds(started.DeadlineMs))).DeadlinesAsync(CancellationToken.None);

        var run = (await ArenaRunStore.GetAsync(Db, Lobby))!;
        Assert.Equal((ArenaPhase.Shop, ArenaRules.Default.StartingCoins), (run.Phase, run.Players[Me].Coins));
        Assert.Contains(run.Players[Me].Character, started.Players[Me].Pool);
        Assert.Equal(run.DeadlineMs, await Db.SortedSetScoreAsync(ArenaRunStore.DeadlinesKey, Lobby));
        Assert.Empty((await KeptConfigAsync())["data"]!["ArenaPlayerInfo"]!["SelectableCharacters"]!.AsArray());
    }

    [SkippableFact]
    // Only an Arena lobby's leader has Start Matchmaking: a member's request, or one from outside an Arena lobby, is refused
    // as a closed queue is, never the catch-all (no ticket: the game waits with every button disabled).
    public async Task AnArenaRequestFromAnyoneButItsLeaderIsRefused()
    {
        Skip.If(_redis is null, "set OVS_TEST_REDIS to run");
        await PlayerAsync(Me);
        await PlayerAsync(Mate);
        await ArenaLobbyAsync(Me, Mate);

        var member = (await Service().RequestAsync("arena-retail", Asking(Mate), CancellationToken.None))!;
        await LobbyAsync(Me, Mate);
        var party = (await Service().RequestAsync("arena-retail", Asking(Me), CancellationToken.None))!;

        foreach (var answer in (MatchmakingAnswer[])[member, party])
        {
            Assert.Equal((200, Js.Stringify(ArenaMatchmaking.Unavailable())), (answer.Status, Js.Stringify(answer.Body)));
            Assert.Null(answer.After);
        }

        Assert.Empty(await TicketsAsync());
    }

    [SkippableFact]
    // The game's cancel (its bot timer's, or Cancel Matchmaking) takes the Arena lobby's ticket out, held by a connected
    // game or not.
    public async Task ACancelTakesTheArenaLobbysTicketOut()
    {
        Skip.If(_redis is null, "set OVS_TEST_REDIS to run");
        await PlayerAsync(Me);
        await PlayerAsync(Mate);
        await ArenaLobbyAsync(Me, Mate);
        var answer = (await Service().RequestAsync("arena-retail", Asking(Me), CancellationToken.None))!;
        await answer.After!();
        await QueuedAsync();

        await Service().CancelAsync((string)answer.Body["id"]!, Asking(Mate), CancellationToken.None);

        Assert.Empty(await TicketsAsync());
    }

    [Fact]
    // Every bot fighter has skins to wear, and none is one the lobbies refuse (PartyService's disabled characters).
    public void TheBotRosterIsPlayable()
    {
        Assert.True(BotFighters.All.Count >= 30, $"{BotFighters.All.Count} fighters");
        Assert.All(BotFighters.All, f => Assert.NotEmpty(f.Skins));
        Assert.DoesNotContain(BotFighters.All, f => f.Character is "character_Meeseeks" or "character_supershaggy" or "character_c022" or "character_C022");
    }
}

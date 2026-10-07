using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenVersus.Server.Core.Clients;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Cosmetics;
using OpenVersus.Server.Core.Identity;
using OpenVersus.Server.Core.Leaderboards;
using OpenVersus.Server.Core.Matches;
using OpenVersus.Server.Core.Matchmaking;
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

        await Db.KeyDeleteAsync(["1v1", "2v2", MatchmakingWorker.Casual1v1, MatchmakingWorker.Casual2v2, MatchmakingWorker.Ffa, $"connections:{Ip}"]);
        await Db.HashDeleteAsync(MatchmakingQueue.QueuedKey, [Me, Mate]);
    }

    private IDatabase Db => _redis!.GetDatabase();

    // FFA always open unless a test says otherwise (FfaScheduleTests has the window).
    private MatchmakingRequestService Service(MongoDB.Driver.IMongoDatabase? mongo = null, FfaSettings? ffa = null, TimeProvider? time = null, bool withRedis = true)
    {
        var collection = new ServiceCollection();
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

    private static readonly string[] s_lists = ["1v1", "2v2", MatchmakingWorker.Casual1v1, MatchmakingWorker.Casual2v2];

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

        Assert.Equal((1, 200), ((int)answer["body"]!["MatchmakingCrc"]!, (int)answer["return_code"]!));
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

        var answer = (await Service().RequestAsync("ffa", Asking(Me), CancellationToken.None))!;

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
    // FFA is solo entry: a lobby of two is refused (not sent to 2v2), and nothing is queued.
    public async Task AnFfaRequestFromALobbyOfTwoIsRefused()
    {
        Skip.If(_redis is null, "set OVS_TEST_REDIS to run");
        await PlayerAsync(Me);
        await PlayerAsync(Mate);
        await LobbyAsync(Me, Mate);

        var answer = (await Service().RequestAsync("ffa", Asking(Me), CancellationToken.None))!;

        Assert.Equal((200, """{"error":"FFA matchmaking requires a solo party"}"""), (answer.Status, Js.Stringify(answer.Body)));
        Assert.Null(answer.After);
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

    [Fact]
    // Every bot fighter has skins to wear, and none is one the lobbies refuse (PartyService's disabled characters).
    public void TheBotRosterIsPlayable()
    {
        Assert.True(BotFighters.All.Count >= 30, $"{BotFighters.All.Count} fighters");
        Assert.All(BotFighters.All, f => Assert.NotEmpty(f.Skins));
        Assert.DoesNotContain(BotFighters.All, f => f.Character is "character_Meeseeks" or "character_supershaggy" or "character_c022" or "character_C022");
    }
}

using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Matches;
using OpenVersus.Server.Core.Matchmaking;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Tests.Matchmaking;

/// <summary>
/// The matchmaking worker's rules on real Redis, database 15 (OVS_TEST_REDIS[_USER/_PW]): one tick at a time against
/// queues the test fills. Parity with the TS worker over whole scenarios is tools/matches/matchmaker_diff.mjs; these pin
/// the rules at their edges.
/// </summary>
[Collection(RedisTestDatabase.Name)]
public sealed class MatchmakingWorkerTests : IAsyncLifetime
{
    private static readonly string? s_redis = Environment.GetEnvironmentVariable("OVS_TEST_REDIS");
    private ConnectionMultiplexer? _redis;
    private static string Id(int n) => $"0000000000000000000e{n:D4}";

    private sealed class Ports : IMatchLauncher
    {
        public List<string> Deployed { get; } = [];

        public Task<LaunchedMatch?> LaunchAsync(MatchLaunch launch, CancellationToken ct) => throw new NotSupportedException();
        public Task<int?> RollbackPortAsync(IDatabase redis) => Task.FromResult<int?>(57001);

        public void DeployIfOnDemand(int port, string matchId) => Deployed.Add(matchId);
    }

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

    // The queues, the heartbeats and every key with a test id; the matches made (fresh ids) are found from the queue.
    private async Task CleanAsync()
    {
        var db = _redis!.GetDatabase();
        var server = _redis.GetServer(_redis.GetEndPoints()[0]);
        foreach (var key in server.Keys(15, "*0000000000000000000e*").Concat(server.Keys(15, "ranked_set:*")).Concat(server.Keys(15, "match:*")))
        {
            await db.KeyDeleteAsync(key);
        }

        await db.KeyDeleteAsync(["1v1", "2v2", MatchmakingWorker.Casual1v1, MatchmakingWorker.Casual2v2, MatchmakingWorker.Ffa, "matchmaking:lock:casual1v1", "matchmaking:lock:casual2v2", "matchmaking:lock:FFA", MatchmakingWorker.HeartbeatsKey, "matchmaking:lock:1v1", "matchmaking:lock:2v2"]);
    }

    private IDatabase Db => _redis!.GetDatabase();

    // FFA always open unless a test says otherwise (the window has tests of its own: FfaScheduleTests).
    private MatchmakingWorker Worker(Ports? ports = null, bool p2p = false, FfaSettings? ffa = null, TimeProvider? time = null) => new(new ServiceCollection().AddSingleton<IConnectionMultiplexer>(_redis!).BuildServiceProvider(),
        ports ?? new Ports(), new TestOptions<MatchmakingSettings>(new MatchmakingSettings()), new TestOptions<RollbackSettings>(new RollbackSettings { P2P = p2p }),
        new TestOptions<FfaSettings>(ffa ?? new FfaSettings { WeekendOnly = false }), time ?? TimeProvider.System, NullLogger<MatchmakingWorker>.Instance);

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>Queues a ticket for players <paramref name="players"/> (their skills), waiting <paramref name="age"/> s.</summary>
    private async Task<string> QueueAsync(string queue, int[] players, int age = 1, double skill = 0, int party = 0)
    {
        string partyId = Id(700 + (party == 0 ? players[0] : party));
        var ticket = new JsonObject
        {
            ["created_at"] = Now - age,
            ["matchType"] = queue,
            ["partyLeaderId"] = Id(players[0]),
            ["matchmakingRequestId"] = Id(500 + players[0]),
            ["partyId"] = partyId,
            ["party_size"] = players.Length,
            ["players"] = new JsonArray([.. players.Select(p => (JsonNode)new JsonObject { ["id"] = Id(p), ["region"] = "MVSI", ["skill"] = skill })]),
        };
        await Db.ListRightPushAsync(queue, Js.Stringify(ticket));
        foreach (int p in players)
        {
            await Db.HashSetAsync($"player:{Id(p)}", "ip", $"198.51.100.{p}");
            await Db.SortedSetAddAsync(MatchmakingWorker.HeartbeatsKey, Id(p), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }

        return partyId;
    }

    private async Task<List<string>> QueuedAsync(string queue) =>
        [.. (await Db.ListRangeAsync(queue)).Select(t => JsonNode.Parse(t.ToString())!["partyId"]!.GetValue<string>())];

    [Theory]
    [InlineData(4.9, 250)]
    [InlineData(5, 500)]
    [InlineData(9.9, 500)]
    [InlineData(10, double.PositiveInfinity)]
    public void TheSkillRangeWidensAt5And10Seconds(double waited, double range) => Assert.Equal(range, MatchmakingWorker.SkillRange(waited));

    [Fact]
    // The stricter of the two ranges: a long wait does not widen it for a player who just queued.
    public async Task A1v1PairMatchesOnlyWithinTheStricterOfTheirRanges()
    {
        if (_redis is null)
        {
            return;
        }

        await QueueAsync("1v1", [1], age: 30, skill: 0);
        await QueueAsync("1v1", [2], age: 1, skill: 300);
        await Worker().TickAsync(Db);
        Assert.Equal(2, (await QueuedAsync("1v1")).Count);

        await CleanAsync();
        await QueueAsync("1v1", [1], age: 30, skill: 0);
        await QueueAsync("1v1", [2], age: 6, skill: 300);
        await Worker().TickAsync(Db);
        Assert.Empty(await QueuedAsync("1v1"));
    }

    [Fact]
    public async Task APlayerWithTwoTicketsKeepsTheNewerAndAPartyOfTwoNeverPlays1v1()
    {
        if (_redis is null)
        {
            return;
        }

        await QueueAsync("1v1", [1, 2], age: 9, party: 50);
        string older = await QueueAsync("1v1", [3], age: 5, party: 60);
        string newer = await QueueAsync("1v1", [3], age: 2, party: 61);
        await Worker().TickAsync(Db);

        // The older ticket is gone; the newer is alone among the solo tickets, and the party of two stays.
        var left = await QueuedAsync("1v1");
        Assert.DoesNotContain(older, left);
        Assert.Contains(newer, left);
        Assert.Equal(2, left.Count);
    }

    [Fact]
    public async Task ASilentPlayersTicketGoesAndABlockedPairIsNotMatched()
    {
        if (_redis is null)
        {
            return;
        }

        string silent = await QueueAsync("1v1", [1], age: 3);
        await QueueAsync("1v1", [2], age: 2);
        await QueueAsync("1v1", [3], age: 1);
        await Db.SortedSetAddAsync(MatchmakingWorker.HeartbeatsKey, Id(1), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 60_000);
        await Db.StringSetAsync($"player:{Id(3)}:blocked", Js.Stringify(new JsonArray(Id(2))));
        await Worker().TickAsync(Db);

        var left = await QueuedAsync("1v1");
        Assert.DoesNotContain(silent, left);
        Assert.Equal(2, left.Count);
    }

    [Fact]
    // Parties whole on a team, index = place * 2 + team, one host, ip from player:{id} (none when it has none), no isBot.
    public async Task The2v2TeamsKeepPartiesTogether()
    {
        if (_redis is null)
        {
            return;
        }

        for (int round = 0; round < 25; round++)
        {
            await CleanAsync();
            string duo = await QueueAsync("2v2", [1, 2], age: 3);
            await QueueAsync("2v2", [3], age: 2);
            await QueueAsync("2v2", [4], age: 1);
            await Db.HashDeleteAsync($"player:{Id(4)}", "ip");
            await Worker().TickAsync(Db);

            Assert.Empty(await QueuedAsync("2v2"));
            string matchId = (string)(await Db.StringGetAsync($"player_ranked_set:{Id(1)}"))!;
            var players = JsonNode.Parse((await Db.StringGetAsync(matchId)).ToString())!["players"]!.AsArray().Select(p => p!.AsObject()).ToList();
            Assert.Single(players.Where(p => p["partyId"]!.GetValue<string>() == duo).Select(p => p["teamIndex"]!.GetValue<int>()).Distinct());
            foreach (int team in new[] { 0, 1 })
            {
                Assert.Equal([team, team + 2], players.Where(p => p["teamIndex"]!.GetValue<int>() == team).Select(p => p["playerIndex"]!.GetValue<int>()).Order());
            }

            Assert.Single(players, p => p["isHost"]!.GetValue<bool>());
            Assert.All(players, p => Assert.False(p.ContainsKey("isBot")));
            Assert.False(players.Single(p => p["playerId"]!.GetValue<string>() == Id(4)).ContainsKey("ip"));
            Assert.Equal("198.51.100.3", players.Single(p => p["playerId"]!.GetValue<string>() == Id(3))["ip"]!.GetValue<string>());
            Assert.Equal(57001, JsonNode.Parse((await Db.StringGetAsync($"match:{matchId}")).ToString())!["rollbackPort"]!.GetValue<int>());
        }
    }

    [Fact]
    // 2v2 takes the range of the longest wait among the candidates: a duo that waited long takes solos far off in skill.
    public async Task A2v2GroupTakesTheRangeOfItsLongestWait()
    {
        if (_redis is null)
        {
            return;
        }

        await QueueAsync("2v2", [1, 2], age: 2, skill: 1000);
        await QueueAsync("2v2", [3], age: 1);
        await QueueAsync("2v2", [4], age: 1);
        await Worker().TickAsync(Db);
        Assert.Equal(3, (await QueuedAsync("2v2")).Count);

        await CleanAsync();
        await QueueAsync("2v2", [1, 2], age: 30, skill: 1000);
        await QueueAsync("2v2", [3], age: 1);
        await QueueAsync("2v2", [4], age: 1);
        await Worker().TickAsync(Db);
        Assert.Empty(await QueuedAsync("2v2"));
    }

    // The match records this test's tick made (the cleanup leaves none from before); the launch stream (match:launched)
    // is no record.
    private async Task<List<JsonObject>> MatchesAsync()
    {
        var server = _redis!.GetServer(_redis.GetEndPoints()[0]);
        var matches = new List<JsonObject>();
        foreach (var key in server.Keys(15, "match:*"))
        {
            if (await Db.KeyTypeAsync(key) != RedisType.String)
            {
                continue;
            }

            matches.Add(JsonNode.Parse((await Db.StringGetAsync(key)).ToString())!.AsObject());
        }

        return matches;
    }

    [Fact]
    // Casual: anyone not blocked, whatever their skill or wait; never rated (no set, a password match) but a 1v1 to play.
    public async Task CasualPairsAnyoneNotBlockedAndIsNeverRated()
    {
        if (_redis is null)
        {
            return;
        }

        await QueueAsync(MatchmakingWorker.Casual1v1, [1], age: 1, skill: 0);
        await QueueAsync(MatchmakingWorker.Casual1v1, [2], age: 1, skill: 5000);
        await Worker().TickAsync(Db);

        Assert.Empty(await QueuedAsync(MatchmakingWorker.Casual1v1));
        var match = Assert.Single(await MatchesAsync());
        Assert.Equal((MatchmakingWorker.Casual1v1, true, "casual"), ((string?)match["matchType"], (bool?)match["isPasswordMatch"], (string?)match["queue"]));
        string matchId = (string)match["matchId"]!;
        var notification = JsonNode.Parse((await Db.StringGetAsync(matchId)).ToString())!;
        Assert.Equal("1v1", (string?)notification["mode"]);
        // Unranked for the TS websocket: no set at its end, the unranked config, not a custom lobby's match.
        Assert.Equal((true, false), ((bool)notification["isCustomGame"]!, (bool)notification["gameplayConfigOverride"]!["bIsCustomGame"]!));
        Assert.False(await Db.KeyExistsAsync($"ranked_set:{matchId}"));
        Assert.False(await Db.KeyExistsAsync($"player_ranked_set:{Id(1)}"));
    }

    [Fact]
    public async Task CasualStillPassesOverABlockedPairAndKeepsItsQueuesApart()
    {
        if (_redis is null)
        {
            return;
        }

        await QueueAsync(MatchmakingWorker.Casual1v1, [1], age: 2);
        await QueueAsync(MatchmakingWorker.Casual1v1, [2], age: 1);
        await QueueAsync("1v1", [3], age: 1);
        await Db.StringSetAsync($"player:{Id(2)}:blocked", Js.Stringify(new JsonArray(Id(1))));
        await Worker().TickAsync(Db);

        Assert.Equal(2, (await QueuedAsync(MatchmakingWorker.Casual1v1)).Count);
        Assert.Single(await QueuedAsync("1v1"));
        Assert.Empty(await MatchesAsync());
    }

    [Fact]
    public async Task ACasual2v2FillsFromPartiesWithoutSkillAndARegularMatchStillStartsASet()
    {
        if (_redis is null)
        {
            return;
        }

        await QueueAsync(MatchmakingWorker.Casual2v2, [1, 2], age: 1, skill: 0);
        await QueueAsync(MatchmakingWorker.Casual2v2, [3, 4], age: 1, skill: 4000);
        await QueueAsync("1v1", [5], age: 1);
        await QueueAsync("1v1", [6], age: 1);
        await Worker().TickAsync(Db);

        Assert.Empty(await QueuedAsync(MatchmakingWorker.Casual2v2));
        var matches = await MatchesAsync();
        Assert.Equal(2, matches.Count);
        var casual = matches.Single(m => (string?)m["matchType"] == MatchmakingWorker.Casual2v2);
        var regular = matches.Single(m => (string?)m["matchType"] == "1v1");
        Assert.False(await Db.KeyExistsAsync($"ranked_set:{(string)casual["matchId"]!}"));
        Assert.True(await Db.KeyExistsAsync($"ranked_set:{(string)regular["matchId"]!}"));
        // Game 1's set keys outlive the longest game, until the TS websocket writes them again at its end (TS: 10 min).
        foreach (string key in new[] { $"ranked_set:{(string)regular["matchId"]!}", $"player_ranked_set:{Id(5)}", $"player_ranked_set:{Id(6)}" })
        {
            Assert.InRange((await Db.KeyTimeToLiveAsync(key))!.Value, TimeSpan.FromMinutes(19), TimeSpan.FromMinutes(20));
        }

        Assert.Null(regular["isPasswordMatch"]);
        var regularNotification = JsonNode.Parse((await Db.StringGetAsync((string)regular["matchId"]!)).ToString())!;
        Assert.Null(regularNotification["isCustomGame"]);
    }

    [Theory]
    // A 1v1 of two humans runs P2P with the switch on: p2p in its config, and no rollback server deployed (the port is
    // kept for the relay). Switched off, the same match is a server match, deployed as before.
    [InlineData(true)]
    [InlineData(false)]
    public async Task A1v1OfTwoHumansRunsP2POnlyWithTheSwitchAndThenDeploysNothing(bool p2p)
    {
        if (_redis is null)
        {
            return;
        }

        await QueueAsync("1v1", [1], age: 1);
        await QueueAsync("1v1", [2], age: 1);
        var ports = new Ports();
        await Worker(ports, p2p).TickAsync(Db);

        string matchId = (string)(await MatchesAsync()).Single()["matchId"]!;
        var notification = JsonNode.Parse((await Db.StringGetAsync(matchId)).ToString())!;
        Assert.Equal(p2p, notification["p2p"]!.GetValue<bool>());
        Assert.Equal(57001, notification["rollbackPort"]!.GetValue<int>());
        Assert.Equal(p2p ? [] : [matchId], ports.Deployed);
    }

    [Fact]
    // Four humans: P2P too (any mode), with nothing deployed.
    public async Task A2v2OfFourHumansRunsP2P()
    {
        if (_redis is null)
        {
            return;
        }

        await QueueAsync("2v2", [1, 2], age: 1);
        await QueueAsync("2v2", [3, 4], age: 1);
        var ports = new Ports();
        await Worker(ports, p2p: true).TickAsync(Db);

        string matchId = (string)(await MatchesAsync()).Single()["matchId"]!;
        Assert.True(JsonNode.Parse((await Db.StringGetAsync(matchId)).ToString())!["p2p"]!.GetValue<bool>());
        Assert.Empty(ports.Deployed);
    }

    [Fact]
    // FFA: the four oldest solo tickets, whatever their skill; a party's ticket never plays. Each player a team of their
    // own in that order, one host, a 2v2 map, mode FFA; no set, and not a password match (as the TS worker writes it).
    public async Task FfaMatchesTheFourOldestSoloPlayersEachOnATeamOfTheirOwn()
    {
        if (_redis is null)
        {
            return;
        }

        string party = await QueueAsync(MatchmakingWorker.Ffa, [2, 3], age: 9);
        await QueueAsync(MatchmakingWorker.Ffa, [1], age: 5, skill: 3000);
        await QueueAsync(MatchmakingWorker.Ffa, [4], age: 4);
        await QueueAsync(MatchmakingWorker.Ffa, [5], age: 3);
        await QueueAsync(MatchmakingWorker.Ffa, [6], age: 2);
        string youngest = await QueueAsync(MatchmakingWorker.Ffa, [7], age: 1);
        await Db.HashDeleteAsync($"player:{Id(6)}", "ip");
        await Worker().TickAsync(Db);

        Assert.Equal([party, youngest], await QueuedAsync(MatchmakingWorker.Ffa));
        var match = Assert.Single(await MatchesAsync());
        Assert.Equal((MatchmakingWorker.Ffa, 4), ((string?)match["matchType"], (int)match["totalPlayers"]!));
        Assert.Null(match["isPasswordMatch"]);
        string matchId = (string)match["matchId"]!;
        var notification = JsonNode.Parse((await Db.StringGetAsync(matchId)).ToString())!;
        Assert.Equal("FFA", (string?)notification["mode"]);
        Assert.Null(notification["isCustomGame"]);
        var players = notification["players"]!.AsArray().Select(p => p!.AsObject()).ToList();
        Assert.Equal([Id(1), Id(4), Id(5), Id(6)], players.Select(p => p["playerId"]!.GetValue<string>()));
        Assert.Equal([0, 1, 2, 3], players.Select(p => p["playerIndex"]!.GetValue<int>()));
        Assert.Equal([0, 1, 2, 3], players.Select(p => p["teamIndex"]!.GetValue<int>()));
        Assert.Equal(Id(701), players[0]["partyId"]!.GetValue<string>());
        Assert.Single(players, p => p["isHost"]!.GetValue<bool>());
        Assert.False(players[3].ContainsKey("ip"));
        Assert.Equal("198.51.100.1", players[0]["ip"]!.GetValue<string>());

        using var stream = typeof(MatchmakingWorker).Assembly.GetManifestResourceStream("OpenVersus.Server.Core.Matchmaking.maps.json")!;
        var maps = JsonNode.Parse(stream)!["2v2"]!.AsArray().Where(m => (bool)m!["enabled"]!).Select(m => (string)m!["id"]!);
        Assert.Contains((string)notification["map"]!, maps);
        Assert.False(await Db.KeyExistsAsync($"ranked_set:{matchId}"));
        Assert.False(await Db.KeyExistsAsync($"player_ranked_set:{Id(1)}"));
    }

    [Fact]
    public async Task FfaWaitsForFourAndPassesOverABlockedGroup()
    {
        if (_redis is null)
        {
            return;
        }

        await QueueAsync(MatchmakingWorker.Ffa, [1], age: 4);
        await QueueAsync(MatchmakingWorker.Ffa, [2], age: 3);
        await QueueAsync(MatchmakingWorker.Ffa, [3], age: 2);
        await Worker().TickAsync(Db);
        Assert.Equal(3, (await QueuedAsync(MatchmakingWorker.Ffa)).Count);

        await QueueAsync(MatchmakingWorker.Ffa, [4], age: 1);
        await Db.StringSetAsync($"player:{Id(4)}:blocked", Js.Stringify(new JsonArray(Id(2))));
        await Worker().TickAsync(Db);
        Assert.Equal(4, (await QueuedAsync(MatchmakingWorker.Ffa)).Count);
        Assert.Empty(await MatchesAsync());
    }

    [Fact]
    // Outside the window (a Tuesday in New York) every FFA ticket goes and its search is cancelled for its players, as
    // cancelMatchmakingForAll, through MatchmakingQueue: the player's tick (realtime:queued) stops instead of searching
    // for a ticket that is gone, and the lobby has to ready again. The other queues are not touched.
    public async Task AClosedFfaQueueCancelsEverySearchInIt()
    {
        if (_redis is null)
        {
            return;
        }

        await Db.KeyDeleteAsync(MatchmakingQueue.QueuedKey);
        await QueueAsync(MatchmakingWorker.Ffa, [1], age: 2);
        await QueueAsync(MatchmakingWorker.Ffa, [2], age: 1);
        await QueueAsync("1v1", [3], age: 1);
        foreach (var (player, index) in new[] { (1, 0), (2, 1) })
        {
            await Db.HashSetAsync(MatchmakingQueue.QueuedKey, Id(player), (string?)await Db.ListGetByIndexAsync(MatchmakingWorker.Ffa, index) ?? "");
        }

        await Db.StringSetAsync($"player_lobby:{Id(1)}", Id(900));
        await Db.StringSetAsync($"party_ready:{Id(900)}", "1");
        var tuesday = new Clock(DateTimeOffset.Parse("2026-10-06T18:00:00Z"));
        await Worker(ffa: new FfaSettings(), time: tuesday).TickAsync(Db);

        Assert.Empty(await QueuedAsync(MatchmakingWorker.Ffa));
        Assert.Single(await QueuedAsync("1v1"));
        Assert.Empty(await MatchesAsync());
        Assert.False(await Db.KeyExistsAsync($"party_ready:{Id(900)}"));
        Assert.False(await Db.HashExistsAsync(MatchmakingQueue.QueuedKey, Id(1)));
        Assert.False(await Db.HashExistsAsync(MatchmakingQueue.QueuedKey, Id(2)));
        await Db.KeyDeleteAsync(MatchmakingQueue.QueuedKey);
    }
}

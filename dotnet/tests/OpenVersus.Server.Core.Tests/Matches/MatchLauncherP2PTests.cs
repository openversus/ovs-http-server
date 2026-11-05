using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenVersus.Server.Core.Matches;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Tests.Matches;

/// <summary>
/// P2P as every C# match start decides it (MatchLauncher.LaunchAsync: custom lobbies, the Casual queue, rift nodes), and
/// a match id named before the launch, on real Redis, database 15 (OVS_TEST_REDIS[_USER/_PW]), with servers deployed on
/// demand so a deploy is visible.
/// </summary>
[Collection(RedisTestDatabase.Name)]
public sealed class MatchLauncherP2PTests : IAsyncLifetime
{
    private static readonly string? s_redis = Environment.GetEnvironmentVariable("OVS_TEST_REDIS");
    private ConnectionMultiplexer? _redis;

    // Records each deploy request as it is sent (MatchLauncher sends it without waiting for the answer).
    private sealed class Webhook : HttpMessageHandler, IHttpClientFactory
    {
        public List<string> Bodies { get; } = [];

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Bodies)
            {
                Bodies.Add(request.Content!.ReadAsStringAsync(ct).GetAwaiter().GetResult());
            }

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
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
    }

    public async Task DisposeAsync()
    {
        if (_redis is not null)
        {
            await _redis.DisposeAsync();
        }
    }

    private static MatchPlayer Human(int n, bool host = false) => new($"0000000000000000000f{n:D4}", n, n % 2, host, $"198.51.100.{n}", IsBot: false);

    private static MatchPlayer Bot(int n) => new($"Bot{n}", n, n % 2, false, "", IsBot: true);

    private static MatchPlayer Spectator(int n) => new($"0000000000000000000f{n:D4}", 8888 + n, -1, false, "", IsBot: false, IsSpectator: true);

    public static TheoryData<string, bool, MatchPlayer[], bool> Launches => new()
    {
        { "two humans, switch on", true, [Human(0, host: true), Human(1)], true },
        { "two humans, switch off", false, [Human(0, host: true), Human(1)], false },
        { "one human and a bot", true, [Human(0, host: true), Bot(1)], true },
        { "two humans and a spectator", true, [Human(0, host: true), Human(1), Spectator(2)], true },
        { "four humans", true, [Human(0, host: true), Human(1), Human(2), Human(3)], true },
        // The most a game holds: four players and four spectators.
        { "four humans and four spectators", true, [Human(0, host: true), Human(1), Human(2), Human(3), Spectator(4), Spectator(5), Spectator(6), Spectator(7)], true },
        { "two humans and two bots", true, [Human(0, host: true), Bot(1), Human(2), Bot(3)], true },
        { "four humans, switch off", false, [Human(0, host: true), Human(1), Human(2), Human(3)], false },
        // Nobody whose node could host.
        { "a spectator and a bot", true, [Spectator(0), Bot(1)], false },
    };

    [Theory]
    [MemberData(nameof(Launches))]
    // p2p is always in the config, true or false; a P2P match keeps its port (for the relay) and deploys nothing. Every
    // human here has reported a node.
    public async Task AMatchRunsP2PWhenEligibleAndSwitchedOnAndThenDeploysNothing(string name, bool enabled, MatchPlayer[] players, bool p2p)
    {
        if (_redis is null)
        {
            return;
        }

        Assert.True(await LaunchAsync(enabled, players, withNode: [.. players.Where(p => !p.IsBot).Select(p => p.PlayerId)]) == p2p, name);
    }

    public static TheoryData<string, MatchPlayer[], string[], string> NodeLaunches => new()
    {
        { "one human without a node", [Human(0, host: true), Human(1)], [Human(0).PlayerId], "" },
        { "the host without a node", [Human(0, host: true), Human(1)], [Human(1).PlayerId], "" },
        { "nobody with a node", [Human(0, host: true), Human(1)], [], "" },
        { "a spectator without a node", [Human(0, host: true), Human(1), Spectator(2)], [Human(0).PlayerId, Human(1).PlayerId], "" },
        { "a node port of 0", [Human(0, host: true), Human(1)], [Human(0).PlayerId], "0" },
    };

    [Theory]
    [MemberData(nameof(NodeLaunches))]
    // A human with no node (RollbackNode = false, an old client, a node that never reported its port) plays on a rollback
    // server, deployed as for any server match (2026-10-19: sent to 127.0.0.1 with nothing there, the game never
    // connected, and the relay, asked for and reached through nodes, could not save it).
    public async Task AMatchWithAHumanWithoutANodeRunsOnARollbackServer(string name, MatchPlayer[] players, string[] withNode, string otherPort)
    {
        if (_redis is null)
        {
            return;
        }

        string[] reportedOther = otherPort.Length > 0 ? [.. players.Where(p => !p.IsBot).Select(p => p.PlayerId).Except(withNode)] : [];
        Assert.False(await LaunchAsync(enabled: true, players, withNode, reportedOther, otherPort), name);
    }

    // Launches the match with the given humans' nodes reported (connections:{id} nodePort 41234; those in
    // reportedOther report otherPort), checks its port and that a server was deployed only for a server match, cleans
    // up, and returns its p2p.
    private async Task<bool> LaunchAsync(bool enabled, MatchPlayer[] players, string[] withNode, string[]? reportedOther = null, string otherPort = "")
    {
        var db = _redis!.GetDatabase();
        foreach (string id in withNode)
        {
            await db.HashSetAsync($"connections:{id}", "nodePort", "41234");
        }

        foreach (string id in reportedOther ?? [])
        {
            await db.HashSetAsync($"connections:{id}", "nodePort", otherPort);
        }

        var webhook = new Webhook();
        var launcher = new MatchLauncher(new ServiceCollection().AddSingleton<IConnectionMultiplexer>(_redis).BuildServiceProvider(),
            new TestOptions<RollbackSettings>(new RollbackSettings { OnDemand = true, P2P = enabled }), webhook, TimeProvider.System,
            NullLogger<MatchLauncher>.Instance);
        try
        {
            var launched = await launcher.LaunchAsync(new MatchLaunch("1v1", "M001_V3", "1v1", players), CancellationToken.None);

            Assert.NotNull(launched);
            var notification = JsonNode.Parse((await db.StringGetAsync(launched.MatchId)).ToString())!;
            await db.KeyDeleteAsync([launched.MatchId, $"match:{launched.MatchId}"]);
            bool p2p = notification["p2p"]!.GetValue<bool>();
            Assert.Equal(launched.RollbackPort, notification["rollbackPort"]!.GetValue<int>());
            lock (webhook.Bodies)
            {
                Assert.Equal(p2p ? 0 : 1, webhook.Bodies.Count);
            }

            return p2p;
        }
        finally
        {
            await db.KeyDeleteAsync([.. players.Select(p => new RedisKey($"connections:{p.PlayerId}"))]);
        }
    }

    [Fact]
    // An id named before the launch (an Arena round's pre-match configs carry it) is the match's: its record, its
    // notification, the answer.
    public async Task AMatchNamedBeforeItsLaunchStartsUnderThatName()
    {
        if (_redis is null)
        {
            return;
        }

        var launcher = new MatchLauncher(new ServiceCollection().AddSingleton<IConnectionMultiplexer>(_redis).BuildServiceProvider(),
            new TestOptions<RollbackSettings>(new RollbackSettings { OnDemand = true, P2P = true }), new Webhook(), TimeProvider.System,
            NullLogger<MatchLauncher>.Instance);
        string named = MongoDB.Bson.ObjectId.GenerateNewId().ToString();

        var launched = await launcher.LaunchAsync(new MatchLaunch("1v1", "M001_V3", "1v1", [Human(0, host: true), Bot(1)], MatchId: named), CancellationToken.None);

        Assert.Equal(named, launched?.MatchId);
        var db = _redis.GetDatabase();
        var notification = JsonNode.Parse((await db.StringGetAsync(named)).ToString())!;
        var match = JsonNode.Parse((await db.StringGetAsync($"match:{named}")).ToString())!;
        await db.KeyDeleteAsync([named, $"match:{named}"]);
        Assert.Equal(named, (string?)notification["matchId"]);
        Assert.Equal(named, (string?)match["matchId"]);
    }
}

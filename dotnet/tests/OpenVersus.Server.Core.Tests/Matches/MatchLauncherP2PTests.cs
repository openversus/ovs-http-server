using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenVersus.Server.Core.Matches;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Tests.Matches;

/// <summary>
/// P2P as every C# match start decides it (MatchLauncher.LaunchAsync: custom lobbies, the Casual queue, rift nodes), on
/// real Redis, database 15 (OVS_TEST_REDIS[_USER/_PW]), with servers deployed on demand so a deploy is visible.
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
        { "one human and a bot", true, [Human(0, host: true), Bot(1)], false },
        { "two humans and a spectator", true, [Human(0, host: true), Human(1), Spectator(2)], false },
        { "four humans", true, [Human(0, host: true), Human(1), Human(2), Human(3)], false },
        // As the TS isP2PEligible: bots beside the two humans do not make a match ineligible.
        { "two humans and two bots", true, [Human(0, host: true), Bot(1), Human(2), Bot(3)], true },
    };

    [Theory]
    [MemberData(nameof(Launches))]
    // p2p is always in the config, true or false; a P2P match keeps its port (for the relay) and deploys nothing.
    public async Task AMatchRunsP2PWhenEligibleAndSwitchedOnAndThenDeploysNothing(string name, bool enabled, MatchPlayer[] players, bool p2p)
    {
        if (_redis is null)
        {
            return;
        }

        var webhook = new Webhook();
        var launcher = new MatchLauncher(new ServiceCollection().AddSingleton<IConnectionMultiplexer>(_redis).BuildServiceProvider(),
            new TestOptions<RollbackSettings>(new RollbackSettings { OnDemand = true, P2P = enabled }), webhook, TimeProvider.System,
            NullLogger<MatchLauncher>.Instance);

        var launched = await launcher.LaunchAsync(new MatchLaunch("1v1", "M001_V3", "1v1", players), CancellationToken.None);

        Assert.NotNull(launched);
        var db = _redis.GetDatabase();
        var notification = JsonNode.Parse((await db.StringGetAsync(launched.MatchId)).ToString())!;
        await db.KeyDeleteAsync([launched.MatchId, $"match:{launched.MatchId}"]);
        Assert.True(notification["p2p"]!.GetValue<bool>() == p2p, name);
        Assert.Equal(launched.RollbackPort, notification["rollbackPort"]!.GetValue<int>());
        lock (webhook.Bodies)
        {
            Assert.Equal(p2p ? 0 : 1, webhook.Bodies.Count);
        }
    }
}

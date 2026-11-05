using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenVersus.Server.Core.Hosting;
using OpenVersus.Server.Core.Realtime;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Tests.Realtime;

/// <summary>
/// The gateway's reaper of the players of nodes that are gone (GatewayReaper, GatewayPresence.ReapAsync) on real Redis,
/// database 15 (OVS_TEST_REDIS[_USER/_PW]): when a node counts as gone, what a reap writes (as a close would have), and
/// what it leaves alone. What the readers do with the event is LobbyDisconnectsTests' and MatchDisconnectsTests'.
/// </summary>
[Collection(RedisTestDatabase.Name)]
public sealed class GatewayReaperTests : IAsyncLifetime
{
    private static readonly string? s_redis = Environment.GetEnvironmentVariable("OVS_TEST_REDIS");
    private ConnectionMultiplexer? _redis;

    // Ids no real player or node has; every key a test makes holds the prefix.
    private const string Prefix = "0000000000000000003e";
    private const string Player = Prefix + "0001", Other = Prefix + "0002", Connection = Prefix + "0c01";
    private const string DeadNode = "test-node-dead:" + Prefix, LiveNode = "test-node-live:" + Prefix;
    private const string Stream = "realtime:connections:" + Prefix;
    private const string Ip = "198.51.100.61";
    private static readonly string s_token = GatewayPresence.TokenHash("the-session-token");

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly Clock _clock = new(DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000));
    private readonly ServiceInstance _self = new();

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
        foreach (var key in server.Keys(15, $"*{Prefix}*"))
        {
            await Db.KeyDeleteAsync(key);
        }

        await Db.KeyDeleteAsync([InstanceRegistry.Key(_self.Id), $"active_ip_accounts:{Ip}"]);
        await Db.SortedSetRemoveAsync(GatewayPresence.Heartbeats, [Player, Other]);
        await Db.SetRemoveAsync(GatewayPresence.OnlinePlayers, [Player, Other]);
    }

    private IDatabase Db => _redis!.GetDatabase();

    private long Ms => _clock.Now.ToUnixTimeMilliseconds();

    private GatewayReaper Reaper() => new(new ServiceCollection().AddSingleton<IConnectionMultiplexer>(_redis!).BuildServiceProvider(),
        new TestOptions<GatewaySettings>(new GatewaySettings { ReapAfterMs = 30_000 }), _self, _clock, NullLogger<GatewayReaper>.Instance)
    { StreamKey = Stream };

    // The player as the gateway left them on their node: connected, online, last answered `silentFor` ago.
    private async Task ConnectedAsync(string player, string node, TimeSpan silentFor, string connection = Connection)
    {
        await Db.HashSetAsync(GatewayPresence.ConnectionKey(player), [new("id", connection), new("node", node), new("ip", Ip), new("at", Ms), new("token", s_token)]);
        await Db.SortedSetAddAsync(GatewayPresence.Heartbeats, player, Ms - silentFor.TotalMilliseconds);
        await Db.SetAddAsync(GatewayPresence.OnlinePlayers, player);
        await Db.SortedSetAddAsync($"active_ip_accounts:{Ip}", player, Ms);
    }

    private Task RegisteredAsync(string node, string state) =>
        Db.StringSetAsync(InstanceRegistry.Key(node), $$"""{"service":"ws","instance":"{{node}}","state":"{{state}}"}""");

    private async Task<List<Dictionary<string, string>>> EventsAsync() =>
        [.. (await Db.StreamRangeAsync(Stream)).Select(e => e.Values.ToDictionary(v => v.Name.ToString(), v => v.Value.ToString()))
            .Where(e => e["player"].StartsWith(Prefix, StringComparison.Ordinal))];

    private async Task AssertOfflineAsync(string player)
    {
        Assert.False(await Db.KeyExistsAsync(GatewayPresence.ConnectionKey(player)));
        Assert.Null(await Db.SortedSetScoreAsync(GatewayPresence.Heartbeats, player));
        Assert.False(await Db.SetContainsAsync(GatewayPresence.OnlinePlayers, player));
        Assert.Null(await Db.SortedSetScoreAsync($"active_ip_accounts:{Ip}", player));
    }

    [Fact]
    // A node whose registry entry is missing on looks 10 s apart is gone (a live one rewrites it every 5 s, also right
    // after a Redis outage let it run out): its silent player is closed as the node's close would have closed them.
    public async Task ThePlayerOfANodeMissingFromTheRegistryIsTakenOfflineWithAReapedDisconnect()
    {
        if (_redis is null)
        {
            return;
        }

        var reaper = Reaper();
        await ConnectedAsync(Player, DeadNode, TimeSpan.FromSeconds(40));

        await reaper.SweepAsync(Db);
        _clock.Now += TimeSpan.FromSeconds(5);
        await reaper.SweepAsync(Db);
        Assert.True(await Db.KeyExistsAsync(GatewayPresence.ConnectionKey(Player)));
        Assert.Empty(await EventsAsync());

        _clock.Now += TimeSpan.FromSeconds(5);
        await reaper.SweepAsync(Db);

        await AssertOfflineAsync(Player);
        var reaped = Assert.Single(await EventsAsync());
        Assert.Equal("disconnected", reaped["type"]);
        Assert.Equal(Connection, reaped["connection"]);
        Assert.Equal(DeadNode, reaped["node"]);
        Assert.Equal(Ip, reaped["ip"]);
        Assert.Equal(s_token, reaped["token"]);
        Assert.Equal(Ms.ToString(), reaped["at"]);
        Assert.Equal("1", reaped["reaped"]);
    }

    [Fact]
    // A node that is up keeps its players, however long they have been silent (closing them is its own cut-off's), and so
    // does this node; one that says it stopped is gone at once (a clean stop closes its connections first).
    public async Task OnlyTheSilentPlayersOfANodeThatIsGoneAreReaped()
    {
        if (_redis is null)
        {
            return;
        }

        var reaper = Reaper();
        await RegisteredAsync(LiveNode, "Ready");
        await ConnectedAsync(Player, LiveNode, TimeSpan.FromMinutes(2));
        await ConnectedAsync(Other, _self.Id, TimeSpan.FromMinutes(2), connection: Prefix + "0c02");
        for (int i = 0; i < 3; i++)
        {
            await reaper.SweepAsync(Db);
            _clock.Now += TimeSpan.FromSeconds(10);
        }

        Assert.True(await Db.KeyExistsAsync(GatewayPresence.ConnectionKey(Player)));
        Assert.True(await Db.KeyExistsAsync(GatewayPresence.ConnectionKey(Other)));
        Assert.Empty(await EventsAsync());

        // Answered 20 s ago, on a node that stopped: still too recent.
        await RegisteredAsync(LiveNode, "Stopped");
        await Db.SortedSetAddAsync(GatewayPresence.Heartbeats, Player, Ms - 20_000);
        await reaper.SweepAsync(Db);
        Assert.Empty(await EventsAsync());

        _clock.Now += TimeSpan.FromSeconds(11);
        await reaper.SweepAsync(Db);
        await AssertOfflineAsync(Player);
        Assert.Equal(Player, Assert.Single(await EventsAsync())["player"]);
    }

    [Fact]
    // The script acts only on the connection it was given, on the node it was given, while still silent: several nodes
    // reaping at once reap a player once, and a connection the edge moved to another node (under the same id), or one
    // that answered since, is left alone.
    public async Task AReapActsOnceAndOnlyOnTheConnectionStillOnTheNodeThatIsGone()
    {
        if (_redis is null)
        {
            return;
        }

        long before = Ms - 30_000;
        await ConnectedAsync(Player, LiveNode, TimeSpan.FromSeconds(40));
        Assert.False(await GatewayPresence.ReapAsync(Db, Player, Connection, DeadNode, Ip, s_token, before, _clock.Now, Stream));
        Assert.False(await GatewayPresence.ReapAsync(Db, Player, Prefix + "0c09", LiveNode, Ip, s_token, before, _clock.Now, Stream));
        Assert.False(await GatewayPresence.ReapAsync(Db, Player, Connection, LiveNode, Ip, s_token, Ms - 60_000, _clock.Now, Stream));
        Assert.Empty(await EventsAsync());

        Assert.True(await GatewayPresence.ReapAsync(Db, Player, Connection, LiveNode, Ip, s_token, before, _clock.Now, Stream));
        Assert.False(await GatewayPresence.ReapAsync(Db, Player, Connection, LiveNode, Ip, s_token, before, _clock.Now, Stream));
        Assert.Single(await EventsAsync());
    }

    [Fact]
    // In a post-match window the player stays online and keeps their IP's session, as at a close (GatewayPresence).
    public async Task APlayerInAPostMatchWindowStaysOnline()
    {
        if (_redis is null)
        {
            return;
        }

        await ConnectedAsync(Player, DeadNode, TimeSpan.FromSeconds(40));
        await Db.StringSetAsync($"rejoin_pending:{Player}", "1", TimeSpan.FromSeconds(45));

        Assert.True(await GatewayPresence.ReapAsync(Db, Player, Connection, DeadNode, Ip, s_token, Ms - 30_000, _clock.Now, Stream));

        Assert.False(await Db.KeyExistsAsync(GatewayPresence.ConnectionKey(Player)));
        Assert.Null(await Db.SortedSetScoreAsync(GatewayPresence.Heartbeats, Player));
        Assert.True(await Db.SetContainsAsync(GatewayPresence.OnlinePlayers, Player));
        Assert.NotNull(await Db.SortedSetScoreAsync($"active_ip_accounts:{Ip}", Player));
        Assert.Single(await EventsAsync());
    }

    [Fact]
    // A silent player with no connection entry left (it runs out 3 minutes after the last answer: no node reaped them) is
    // taken offline with an event naming no connection, node or session token.
    public async Task APlayerWithNoConnectionLeftIsTakenOfflineAtTheFirstLook()
    {
        if (_redis is null)
        {
            return;
        }

        await ConnectedAsync(Player, DeadNode, TimeSpan.FromMinutes(4));
        await Db.KeyDeleteAsync(GatewayPresence.ConnectionKey(Player));

        await Reaper().SweepAsync(Db);

        // Its IP went with the entry: the IP's session list drops it after 90 s on its own (TouchSessionAsync).
        Assert.Null(await Db.SortedSetScoreAsync(GatewayPresence.Heartbeats, Player));
        Assert.False(await Db.SetContainsAsync(GatewayPresence.OnlinePlayers, Player));
        var reaped = Assert.Single(await EventsAsync());
        Assert.Equal(("", "", "", ""), (reaped["connection"], reaped["node"], reaped["token"], reaped["ip"]));
        Assert.Equal("1", reaped["reaped"]);
    }
}

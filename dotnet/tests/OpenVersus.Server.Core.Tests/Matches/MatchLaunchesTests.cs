using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenVersus.Server.Core.Access;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Matches;
using OpenVersus.Server.Core.Realtime;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Tests.Matches;

/// <summary>
/// How a match is announced (<see cref="MatchLaunches"/>) and what the match flow tells its players from match:launched
/// (<see cref="MatchLaunchStream"/>, Realtime:Gateway on): where each game is sent to connect, in what order, once. The
/// config itself is GameplayConfigs' (a stand-in here); parity with the TS websocket is tools/matches/config_diff.mjs.
/// Real Redis (database 15, OVS_TEST_REDIS).
/// </summary>
[Collection(RedisTestDatabase.Name)]
public sealed class MatchLaunchesTests : IAsyncLifetime
{
    private static readonly string? s_redis = Environment.GetEnvironmentVariable("OVS_TEST_REDIS");
    private static string Id(int n) => $"00000000000000000019{n:D4}";
    private static readonly string Match = Id(100), P1 = Id(1), P2 = Id(2), Bot = Id(11), Spectator = Id(21);
    private const string Relay = "203.0.113.50";

    private ConnectionMultiplexer? _redis;
    private readonly ConcurrentQueue<JsonObject> _sent = new();
    private readonly ConcurrentQueue<string> _published = new();

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
        // Channels ignore the database: only this class's players count (a cancel names their request, not the match).
        await _redis.GetSubscriber().SubscribeAsync(RedisChannel.Literal(ProfileNotifications.WsSendChannel), (_, m) =>
        {
            if (m.ToString().Contains("00000000000000000019", StringComparison.Ordinal) && Js.Parse(m.ToString()) is JsonObject send)
            {
                _sent.Enqueue(send);
            }
        });
        await _redis.GetSubscriber().SubscribeAsync(RedisChannel.Literal(MatchLauncher.NotificationChannel), (_, m) =>
        {
            if (m.ToString().Contains(Match, StringComparison.Ordinal))
            {
                _published.Enqueue(m.ToString());
            }
        });
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
        foreach (var key in server.Keys(15, "*00000000000000000019*"))
        {
            await Db.KeyDeleteAsync(key);
        }

        await Db.KeyDeleteAsync(MatchLaunches.Stream);
        // realtime:due is shared with other classes: only this class's entries go.
        foreach (var member in await Db.SortedSetRangeByScoreAsync(DelayedMessages.Key))
        {
            if (member.ToString().Contains("00000000000000000019", StringComparison.Ordinal))
            {
                await Db.SortedSetRemoveAsync(DelayedMessages.Key, member);
            }
        }
    }

    private IDatabase Db => _redis!.GetDatabase();

    private IServiceProvider Services(bool gateway) => new ServiceCollection()
        .AddSingleton<IConnectionMultiplexer>(_redis!)
        .AddSingleton<IOptionsMonitor<RealtimeSettings>>(new TestOptions<RealtimeSettings>(new RealtimeSettings { Gateway = gateway }))
        .BuildServiceProvider();

    public enum Build { Config, Fails, Nothing }

    // A stand-in for GameplayConfigs: a config naming the match, a failure (Mongo), or none (no map, no players).
    private sealed class Configs(Build build) : IGameplayConfigs
    {
        public Task<JsonObject?> BuildAsync(JsonObject notification, GameplayConfigMode mode, CancellationToken ct) => build switch
        {
            Build.Fails => throw new InvalidOperationException("Mongo is down"),
            Build.Nothing => Task.FromResult<JsonObject?>(null),
            _ => Task.FromResult<JsonObject?>(new JsonObject { ["data"] = new JsonObject { ["MatchId"] = notification["matchId"]?.DeepClone(), ["template_id"] = "OnGameplayConfigNotified", ["mode"] = mode.ToString() } }),
        };

        public Task PerksLockedAsync(JsonObject notification, CancellationToken ct) => Task.CompletedTask;
    }

    private MatchLaunchStream Stream(Build build = Build.Config) => new(Services(true), new Configs(build),
        new TestOptions<RollbackSettings>(new RollbackSettings { UdpServerIp = Relay, UdpPort = 7777 }), TimeProvider.System, NullLogger<MatchLaunchStream>.Instance);

    // The players' party searched with request req-1 (a matchmaker ticket); the spectator came with none.
    private static readonly MatchComplete[] s_parties = [new([P1, P2], JsonValue.Create("req-1"), Searching: true)];
    private const string PartiesJson = """[{"playerIds":["000000000000000000190001","000000000000000000190002"],"requestId":"req-1","searching":true}]""";
    // The same match started by a launcher (a custom lobby): a request the game never made.
    private const string LaunchedJson = """[{"playerIds":["000000000000000000190001","000000000000000000190002"],"requestId":"req-2","searching":false}]""";

    private static string? Template(JsonObject send) => (string?)send["message"]!["data"]?["template_id"] ?? (string?)send["message"]!["cmd"];

    private static JsonObject Player(string id, int index, bool bot = false, bool spectator = false)
    {
        var player = new JsonObject { ["playerId"] = id, ["partyId"] = Match, ["playerIndex"] = index, ["teamIndex"] = index % 2, ["isHost"] = index == 0, ["ip"] = "198.51.100.9" };
        player[spectator ? "isSpectator" : "isBot"] = spectator || bot;
        return player;
    }

    private static string Notification(bool p2p = false) => Js.Stringify(new JsonObject
    {
        ["players"] = new JsonArray(Player(P1, 0), Player(P2, 1), Player(Bot, 3, bot: true), Player(Spectator, 8888, spectator: true)),
        ["matchId"] = Match,
        ["matchKey"] = "the-key",
        ["map"] = "M001_V2",
        ["mode"] = "1v1",
        ["rollbackPort"] = 57003,
        ["p2p"] = p2p,
    });

    private async Task<List<JsonObject>> SentAsync(int expected)
    {
        for (int i = 0; i < 50 && _sent.Count < expected; i++)
        {
            await Task.Delay(20);
        }

        await Task.Delay(100);
        return [.. _sent];
    }

    private static string Ids(JsonObject send) => string.Join(",", (send["playerIds"] as JsonArray ?? []).Select(n => (string)n!));

    private static (string Address, int Port) Server(JsonObject send) =>
        ((string)send["message"]!["data"]!["IPAddress"]!, (int)send["message"]!["data"]!["Port"]!);

    [SkippableFact]
    public async Task TheGatewayTakesTheLaunchFromTheStreamInsteadOfTheChannel()
    {
        Skip.If(_redis is null, "OVS_TEST_REDIS not set");
        // Off: published, and matchmaking-complete sent at once (the TS websocket tells the players the rest).
        await MatchLaunches.AnnounceAsync(Services(gateway: false), Db, Match, Notification(), s_parties);
        var complete = Assert.Single(await SentAsync(1));
        Assert.Equal(("matchmaking-complete", $"{P1},{P2}", "req-1"), (Template(complete), Ids(complete), (string?)complete["message"]!["payload"]!["id"]));
        Assert.Single(_published);
        Assert.Equal(0, await Db.StreamLengthAsync(MatchLaunches.Stream));
        _sent.Clear();

        await MatchLaunches.AnnounceAsync(Services(gateway: true), Db, Match, Notification(), s_parties);
        await Task.Delay(200);
        Assert.Single(_published);
        Assert.Empty(_sent);
        var entry = Assert.Single(await Db.StreamRangeAsync(MatchLaunches.Stream));
        Assert.Equal((Match, PartiesJson), ((string?)entry["match"], (string?)entry["complete"]));
        Assert.Equal(Notification(), (string?)await Db.StringGetAsync(Match));

        // The match flow reads it once, tells the players, and acknowledges it.
        var stream = Stream();
        await stream.EnsureGroupAsync(Db);
        Assert.Equal(1, await stream.ReadAsync(Db, CancellationToken.None));
        Assert.Equal(0, await stream.ReadAsync(Db, CancellationToken.None));
        Assert.Equal(0, (await Db.StreamPendingAsync(MatchLaunches.Stream, MatchLaunchStream.Group)).PendingMessageCount);
        Assert.Equal(5, (await SentAsync(5)).Count);
    }

    [SkippableFact]
    public async Task EachGameIsToldWhereToConnectThenMatchmakingCompleteThenTheConfig()
    {
        Skip.If(_redis is null, "OVS_TEST_REDIS not set");
        await Db.StringSetAsync(Match, Notification());
        // P1's game connected through a proxy from elsewhere, P2's from the server's own machine; the spectator's
        // connection is gone (no entry: not loopback).
        await Db.HashSetAsync(GatewayPresence.ConnectionKey(P1), "ip", "198.51.100.1");
        await Db.HashSetAsync(GatewayPresence.ConnectionKey(P2), "ip", "::1");
        await Stream().AnnounceAsync(Db, Match, Notification(), PartiesJson, CancellationToken.None);

        var sent = await SentAsync(5);
        Assert.Equal([P1, P2, Spectator, $"{P1},{P2}", $"{P1},{P2},{Spectator}"], sent.Select(Ids));
        Assert.Equal("matchmaking-complete", Template(sent[3]));
        Assert.All(sent.Take(3), s => Assert.Equal(MatchLaunches.GameServerReadyTemplate, (string?)s["message"]!["data"]!["template_id"]));
        Assert.Equal((Relay, 57003), Server(sent[0]));
        Assert.Equal(("127.0.0.1", 57003), Server(sent[1]));
        Assert.Equal((Relay, 57003), Server(sent[2]));
        Assert.Equal("""{"data":{"MatchKey":"the-key","MatchID":"000000000000000000190100","Port":57003,"template_id":"GameServerReadyNotification","IPAddress":"203.0.113.50"},"payload":{"match":{"id":"000000000000000000190100"},"custom_notification":"realtime"},"header":"","cmd":"update"}""",
            Js.Stringify(sent[0]["message"]));
        // The config as built with Mode On (the TS websocket's writes beside it are the match flow's now).
        Assert.Equal("On", (string?)sent[4]["message"]!["data"]!["mode"]);
    }

    [SkippableFact]
    public async Task AP2PGameIsSentToItsOwnNode()
    {
        Skip.If(_redis is null, "OVS_TEST_REDIS not set");
        await Db.StringSetAsync(Match, Notification(p2p: true));
        await Db.HashSetAsync(GatewayPresence.ConnectionKey(P1), "ip", "198.51.100.1");
        await Db.HashSetAsync($"connections:{P1}", "nodePort", "41000");
        await Db.HashSetAsync($"connections:{P2}", "nodePort", "not a port");
        await Stream().AnnounceAsync(Db, Match, Notification(p2p: true), null, CancellationToken.None);

        var sent = await SentAsync(4);
        Assert.Equal(("127.0.0.1", 41000), Server(sent[0]));
        Assert.Equal(("127.0.0.1", 41234), Server(sent[1]));
    }

    [SkippableFact]
    public async Task AMatchIsAnnouncedOnceAndNotAfterItIsOver()
    {
        Skip.If(_redis is null, "OVS_TEST_REDIS not set");
        var stream = Stream();
        await stream.AnnounceAsync(Db, Match, Notification(), PartiesJson, CancellationToken.None);
        Assert.Empty(await SentAsync(1));

        await Db.StringSetAsync(Match, Notification());
        await stream.AnnounceAsync(Db, Match, Notification(), PartiesJson, CancellationToken.None);
        await stream.AnnounceAsync(Db, Match, Notification(), PartiesJson, CancellationToken.None);
        Assert.Equal(5, (await SentAsync(10)).Count);
    }

    [SkippableTheory]
    [InlineData(Build.Fails)]
    [InlineData(Build.Nothing)]
    public async Task AMatchWithoutAConfigIsCalledOffAndItsPlayersSentBack(Build build)
    {
        Skip.If(_redis is null, "OVS_TEST_REDIS not set");
        await Db.StringSetAsync(Match, Notification());
        await Db.StringSetAsync($"match:{Match}", "{}");
        // Game 1 of a ranked set (the matchmaker writes it with the match); P2's pointer names another set already.
        await Db.StringSetAsync($"ranked_set:{Match}", Js.Stringify(new JsonObject { ["players"] = new JsonArray(Player(P1, 0), Player(P2, 1)) }));
        await Db.StringSetAsync($"ranked_set_checkins:{Match}", "[]");
        await Db.StringSetAsync($"player_ranked_set:{P1}", Match);
        await Db.StringSetAsync($"player_ranked_set:{P2}", Id(999));
        await Db.HashSetAsync(GatewayPresence.ConnectionKey(Spectator), "id", "conn-21");
        await Stream(build).AnnounceAsync(Db, Match, Notification(), PartiesJson, CancellationToken.None);

        // Nobody hears of the match. The players still searching are cancelled, for their party's request.
        var sent = await SentAsync(2);
        Assert.Equal([P1, P2], sent.Select(Ids));
        Assert.All(sent, s => Assert.Equal("matchmaking-cancel", Template(s)));
        Assert.Equal(["req-1", "req-1"], sent.Select(s => (string?)s["message"]!["payload"]!["id"]));
        Assert.Equal(3, (int)sent[0]["message"]!["payload"]!["state"]!);
        // Each is told why by the OpenVersus client; the spectator, whose game cannot be cancelled, that it goes to the
        // title screen, and its connection (the one open now) is closed after the banner.
        foreach (var (player, message) in new[] { (P1, MatchLaunches.CancelledMessage), (P2, MatchLaunches.CancelledMessage), (Spectator, MatchLaunches.ClosedMessage) })
        {
            var banner = Js.Parse((string?)await Db.ListGetByIndexAsync($"dll_notifications:{player}", 0) ?? "null")!;
            Assert.Equal(("admin_banner", MatchLaunches.CancelledTitle, message), ((string?)banner["type"], (string?)banner["title"], (string?)banner["message"]));
        }

        Assert.Equal(["""{"playerId":"000000000000000000190021","connectionId":"conn-21","code":1000,"reason":"match-cancelled"}"""], await DisconnectsAsync());

        // The match and its set are gone; the pointer to another set is not this one's to take.
        foreach (string key in new[] { Match, $"match:{Match}", $"ranked_set:{Match}", $"ranked_set_checkins:{Match}", $"player_ranked_set:{P1}" })
        {
            Assert.False(await Db.KeyExistsAsync(key), key);
        }

        Assert.Equal(Id(999), (string?)await Db.StringGetAsync($"player_ranked_set:{P2}"));
    }

    [SkippableFact]
    public async Task ALauncherStartWithoutAConfigClosesEveryGameAfterTheBanner()
    {
        Skip.If(_redis is null, "OVS_TEST_REDIS not set");
        await Db.StringSetAsync(Match, Notification());
        await Db.HashSetAsync(GatewayPresence.ConnectionKey(P1), "id", "conn-1");
        await Stream(Build.Nothing).AnnounceAsync(Db, Match, Notification(), LaunchedJson, CancellationToken.None);

        // No cancel (the games are on their loading screen, not searching): a banner each, then the close, the TS
        // websocket's way when the player's connection is not the gateway's (no connection named).
        Assert.Empty(await SentAsync(1));
        Assert.Equal(
            ["""{"playerId":"000000000000000000190001","connectionId":"conn-1","code":1000,"reason":"match-cancelled"}""",
             """{"playerId":"000000000000000000190002","code":1000,"reason":"match-cancelled"}""",
             """{"playerId":"000000000000000000190021","code":1000,"reason":"match-cancelled"}"""],
            await DisconnectsAsync());

        // Due 5 s after: the sweep publishes each on ws:disconnect, as it is.
        var published = new ConcurrentQueue<string>();
        await _redis!.GetSubscriber().SubscribeAsync(RedisChannel.Literal(GatewayChannels.Disconnect), (_, m) =>
        {
            if (m.ToString().Contains("00000000000000000019", StringComparison.Ordinal))
            {
                published.Enqueue(m.ToString());
            }
        });
        var sweep = new DelayedMessageSweep(Services(true), new Later(TimeSpan.FromSeconds(6)), NullLogger<DelayedMessageSweep>.Instance);
        await sweep.SweepAsync(Db);
        await Task.Delay(200);
        Assert.Equal(3, published.Count);
        Assert.Empty(await DisconnectsAsync());
    }

    // The ws:disconnect requests waiting in realtime:due for this class's players, in player order.
    private async Task<List<string>> DisconnectsAsync() =>
        [.. (await Db.SortedSetRangeByScoreAsync(DelayedMessages.Key))
            .Select(m => Js.Parse(m.ToString()) as JsonObject)
            .Where(e => (string?)e?["channel"] == GatewayChannels.Disconnect && e!["message"]!.ToJsonString().Contains("00000000000000000019", StringComparison.Ordinal))
            .Select(e => Js.Stringify(e!["message"]))
            .Order()];

    // The clock some time from now.
    private sealed class Later(TimeSpan by) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + by;
    }

    [SkippableFact]
    public async Task ASetsNextGameWithoutAConfigEndsTheSet()
    {
        Skip.If(_redis is null, "OVS_TEST_REDIS not set");
        string set = Id(500);
        await Db.StringSetAsync(Match, Notification());
        await Db.StringSetAsync($"match_to_set:{Match}", set);
        await Db.StringSetAsync($"ranked_set:{set}", Js.Stringify(new JsonObject { ["players"] = new JsonArray(Player(P1, 0), Player(P2, 1)) }));
        await Db.StringSetAsync($"player_ranked_set:{P1}", set);
        await Db.StringSetAsync($"player_ranked_set:{P2}", set);
        await Stream(Build.Nothing).AnnounceAsync(Db, Match, Notification(), null, CancellationToken.None);

        Assert.Empty(await SentAsync(1));
        Assert.Equal(3, (await DisconnectsAsync()).Count);
        foreach (string key in new[] { Match, $"match_to_set:{Match}", $"ranked_set:{set}", $"player_ranked_set:{P1}", $"player_ranked_set:{P2}" })
        {
            Assert.False(await Db.KeyExistsAsync(key), key);
        }
    }
}

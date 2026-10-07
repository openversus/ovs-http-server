using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Matches;
using Microsoft.Extensions.Options;
using OpenVersus.Server.Core.Access;
using OpenVersus.Server.Core.Realtime;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Tests.Matches;

/// <summary>
/// A rollback server's and the P2P nodes' calls about a match (<see cref="IRollbackCallbacks"/>): the registry, who is
/// told to connect and when, the relay, the match's end and start. Parity with the TS server over whole scenarios is
/// tools/matches/callbacks_diff.mjs. Real Redis, database 15 (OVS_TEST_REDIS).
/// </summary>
[Collection(RedisTestDatabase.Name)]
public sealed class RollbackCallbacksTests : IAsyncLifetime
{
    private static readonly string? s_redis = Environment.GetEnvironmentVariable("OVS_TEST_REDIS");
    private static string Id(int n) => $"00000000000000000016{n:D4}";
    private static readonly string Match = Id(100), P1 = Id(1), P2 = Id(2), Bot = Id(3), Spectator = Id(4);
    private const string Key = "the-games-key";

    private ConnectionMultiplexer? _redis;

    // Records each match it is asked to end.
    private sealed class Ender : IMatchEnd
    {
        public List<(string MatchId, string Players)> Ended { get; } = [];

        public Task EndAsync(string matchId, IReadOnlyList<string> playerIds, CancellationToken ct)
        {
            Ended.Add((matchId, string.Join(",", playerIds)));
            return Task.CompletedTask;
        }
    }

    private sealed class Launcher : IMatchLauncher
    {
        public List<(int Port, string MatchId)> Deployed { get; } = [];

        public Task<LaunchedMatch?> LaunchAsync(MatchLaunch launch, CancellationToken ct) => throw new NotSupportedException();
        public Task<int?> RollbackPortAsync(IDatabase redis) => throw new NotSupportedException();
        public void DeployIfOnDemand(int port, string matchId) => Deployed.Add((port, matchId));
    }

    // Signs with a marker, and keeps what it was asked to sign.
    private sealed class Signer(bool hasKey = true) : INodeConfig
    {
        public List<byte[]> Signed { get; } = [];

        public (byte[] Body, string Signature)? Answer() => null;

        public string? Sign(ReadOnlySpan<byte> body)
        {
            Signed.Add(body.ToArray());
            return hasKey ? "signature-of-" + body.Length : null;
        }
    }

    private static bool Configured => !string.IsNullOrEmpty(s_redis);

    public async Task InitializeAsync()
    {
        if (!Configured)
        {
            return;
        }

        string[] parts = s_redis!.Split(':');
        var options = new ConfigurationOptions { EndPoints = { { parts[0], parts.Length > 1 ? int.Parse(parts[1]) : 6379 } }, DefaultDatabase = 15 };
        options.User = Environment.GetEnvironmentVariable("OVS_TEST_REDIS_USER");
        options.Password = Environment.GetEnvironmentVariable("OVS_TEST_REDIS_PW");
        _redis = await ConnectionMultiplexer.ConnectAsync(options);
        await CleanAsync();

        // The queue form keeps the order of publication (a handler subscription may run its callbacks concurrently), which
        // the tests read the sends in.
        (await _redis.GetSubscriber().SubscribeAsync(RedisChannel.Literal(ProfileNotifications.WsSendChannel))).OnMessage(m =>
        {
            if (m.Message.ToString().Contains("00000000000000000016", StringComparison.Ordinal))
            {
                _sent.Enqueue((JsonObject)JsonNode.Parse(m.Message.ToString())!);
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
        foreach (var key in server.Keys(15, "*00000000000000000016*"))
        {
            await Db.KeyDeleteAsync(key);
        }

        // realtime:due is shared with other classes: only this class's entries go.
        foreach (var member in await Db.SortedSetRangeByScoreAsync(DelayedMessages.Key))
        {
            if (member.ToString().Contains("00000000000000000016", StringComparison.Ordinal))
            {
                await Db.SortedSetRemoveAsync(DelayedMessages.Key, member);
            }
        }
    }

    private IDatabase Db => _redis!.GetDatabase();

    private RollbackCallbacks Callbacks(Launcher? launcher = null, Signer? signer = null, RollbackSettings? settings = null, IMatchEnd? matchEnd = null)
    {
        var services = new ServiceCollection().AddSingleton<IConnectionMultiplexer>(_redis!);
        if (matchEnd is not null)
        {
            services.AddSingleton(matchEnd);
        }

        return new(services.BuildServiceProvider(),
            launcher ?? new Launcher(), signer ?? new Signer(),
            new TestOptions<RollbackSettings>(settings ?? new RollbackSettings { UdpServerIp = "203.0.113.5", UdpPort = 41234, P2PNodePort = 41999 }), TimeProvider.System,
            NullLogger<RollbackCallbacks>.Instance);
    }

    // Both players connected (realtime:conn:{player}); the spectator is not waited for, the bot has no game.
    private async Task ConnectedAsync()
    {
        await Db.HashSetAsync(GatewayPresence.ConnectionKey(P1), "id", "c1");
        await Db.HashSetAsync(GatewayPresence.ConnectionKey(P2), "id", "c2");
    }

    // What the games were sent through ws:send, by player.
    private readonly ConcurrentQueue<JsonObject> _sent = new();

    private async Task<List<(string Player, JsonObject Message)>> SentAsync(int waitMs = 200)
    {
        await Task.Delay(waitMs);
        return [.. _sent.SelectMany(s => (s["playerIds"] as JsonArray ?? []).Select(id => ((string)id!, (JsonObject)s["message"]!)))];
    }

    private static JsonArray Roster() =>
    [
        new JsonObject { ["playerId"] = P1, ["playerIndex"] = 0, ["teamIndex"] = 0, ["isHost"] = true, ["ip"] = "198.51.100.1", ["isBot"] = false },
        new JsonObject { ["playerId"] = P2, ["playerIndex"] = 1, ["teamIndex"] = 1, ["isHost"] = false, ["ip"] = "198.51.100.2", ["isBot"] = false },
        new JsonObject { ["playerId"] = Bot, ["playerIndex"] = 3, ["teamIndex"] = 1, ["isHost"] = false, ["ip"] = "", ["isBot"] = true },
        new JsonObject { ["playerId"] = Spectator, ["playerIndex"] = 8888, ["teamIndex"] = -1, ["isHost"] = false, ["ip"] = "198.51.100.4", ["isSpectator"] = true },
    ];

    private async Task SeedAsync(JsonArray? players = null, bool p2p = false, bool withMatch = true)
    {
        await Db.StringSetAsync(Match, Js.Stringify(new JsonObject
        {
            ["players"] = players ?? Roster(),
            ["matchId"] = Match,
            ["matchKey"] = Key,
            ["mode"] = "1v1",
            ["rollbackPort"] = 57003,
            ["p2p"] = p2p,
        }));
        if (withMatch)
        {
            await Db.StringSetAsync($"match:{Match}", Js.Stringify(new JsonObject { ["matchId"] = Match, ["rollbackPort"] = 57005 }));
        }

        await Db.HashSetAsync($"connections:{P1}", [new HashEntry("username", "Player1"), new HashEntry("character", "character_jake")]);
        await Db.HashSetAsync($"connections:{P2}", [new HashEntry("username", "Pläyer ✓ 2"), new HashEntry("character", "")]);
    }

    private static JsonObject Body(string? key = Key) => new() { ["matchId"] = Match, ["key"] = key };

    [SkippableFact]
    public async Task AnUnknownMatchAWrongKeyOrNoBodyIsAnsweredWithNothing()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS to run");
        await SeedAsync();
        var callbacks = Callbacks();

        Assert.Null(await callbacks.RegisterAsync(new JsonObject { ["matchId"] = Id(999), ["key"] = Key }));
        Assert.Null(await callbacks.RegisterAsync(Body("another key")));
        Assert.Null(await callbacks.RegisterAsync(new JsonObject { ["matchId"] = Match }));
        Assert.Null(await callbacks.RegisterAsync(null));
        Assert.Null(await callbacks.RegisterLegacyAsync(Body("another key")));
        Assert.Null(await callbacks.P2PFailedAsync(Body("another key")));
    }

    [SkippableFact]
    public async Task TheRegistryHasTheHumansAndSpectatorsNeverTheBotsAndIsSignedOverItsExactBytes()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS to run");
        await SeedAsync();
        var signer = new Signer();

        var registration = await Callbacks(signer: signer).RegisterAsync(Body());

        Assert.NotNull(registration);
        // As the TS route's JSON.stringify: bots left out (max_players counts the rest), the name and fighter from the
        // connection ("Unknown" for a missing or empty one), the fields in this order.
        Assert.Equal("""{"max_players":3,"match_duration":36000,"players":[""" +
            $$"""{"player_index":0,"player_id":"{{P1}}","player_name":"Player1","player_character":"character_jake","ip":"198.51.100.1","is_host":true,"is_spectator":false},""" +
            $$"""{"player_index":1,"player_id":"{{P2}}","player_name":"Pläyer ✓ 2","player_character":"Unknown","ip":"198.51.100.2","is_host":false,"is_spectator":false},""" +
            $$"""{"player_index":8888,"player_id":"{{Spectator}}","player_name":"Unknown","player_character":"Unknown","ip":"198.51.100.4","is_host":false,"is_spectator":true}]}""",
            Encoding.UTF8.GetString(registration.Body));
        Assert.Equal(registration.Body, Assert.Single(signer.Signed));
        Assert.Equal("signature-of-" + registration.Body.Length, registration.Signature);
        Assert.Equal([P1, P2, Spectator], registration.Ready!.PlayerIds.Select(id => (string?)id));
    }

    [SkippableFact]
    public async Task AMissingFieldIsLeftOutAndANullOneKeptAsJsonStringifyWritesThem()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS to run");
        await SeedAsync(players:
        [
            new JsonObject { ["playerId"] = P2, ["playerIndex"] = null, ["isSpectator"] = null, ["isBot"] = "" },
            new JsonObject { ["playerId"] = Bot, ["playerIndex"] = 2, ["isBot"] = "yes" },
            new JsonObject { ["playerIndex"] = 5, ["isBot"] = 0, ["isHost"] = null },
        ]);

        var registration = await Callbacks().RegisterAsync(Body());

        // An empty string and 0 are falsy (humans), "yes" truthy (a bot); a null isSpectator is false (??), a null
        // playerIndex or isHost stays null, a missing ip or playerId is left out.
        Assert.Equal("""{"max_players":2,"match_duration":36000,"players":[""" +
            $$"""{"player_index":null,"player_id":"{{P2}}","player_name":"Pläyer ✓ 2","player_character":"Unknown","is_spectator":false},""" +
            """{"player_index":5,"player_name":"Unknown","player_character":"Unknown","is_host":null,"is_spectator":false}]}""",
            Encoding.UTF8.GetString(registration!.Body));
        Assert.Equal($$"""["{{P2}}",null]""", registration.Ready!.PlayerIds.ToJsonString());
    }

    [SkippableFact]
    public async Task WithoutASigningKeyTheRegistryGoesUnsigned()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS to run");
        await SeedAsync();

        var registration = await Callbacks(signer: new Signer(hasKey: false)).RegisterAsync(Body());

        Assert.NotNull(registration);
        Assert.Null(registration.Signature);
    }

    [SkippableFact]
    public async Task EachGameIsToldItsServerWithTheMatchsOwnRollbackPort()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS to run");
        await SeedAsync();
        await ConnectedAsync();
        var callbacks = Callbacks();

        await callbacks.ReleaseAsync((await callbacks.RegisterAsync(Body()))!.Ready!);

        var sent = await SentAsync();
        Assert.Equal([P1, P2, Spectator], sent.Select(s => s.Player));
        string id = sent[0].Message["payload"]!["game_server_instance"]!["id"]!.GetValue<string>();
        Assert.Matches("^[0-9a-f]{24}$", id);
        // As the TS websocket built it: the relay host (no player-on-this-machine branch) and match:{id}'s port (the TS
        // redisGetGamePort), not the notification's.
        Assert.All(sent, s => Assert.Equal(
            $$"""{"data":{},"payload":{"game_server_instance":{"game_server_type_slug":"multiplay","port":57005,"owner_id":"{{Match}}","host":"203.0.113.5","id":"{{id}}"},"proxied_data":null},"header":"Your game server is ready to join.","cmd":"game-server-instance-ready"}""",
            Js.Stringify(s.Message)));
    }

    [SkippableFact]
    public async Task AP2PGameIsSentToItsOwnNode()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS to run");
        await SeedAsync(p2p: true);
        await ConnectedAsync();
        await Db.HashSetAsync($"connections:{P1}", "nodePort", "41000");

        await Callbacks().P2PReadyAsync(Body());

        var sent = await SentAsync();
        Assert.Equal([(P1, "127.0.0.1", 41000), (P2, "127.0.0.1", 41999), (Spectator, "127.0.0.1", 41999)],
            sent.Select(s => (s.Player, s.Message["payload"]!["game_server_instance"]!["host"]!.GetValue<string>(), s.Message["payload"]!["game_server_instance"]!["port"]!.GetValue<int>())));
    }

    [SkippableFact]
    public async Task APlayerGoneReleasesTheOthers()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS to run");
        await SeedAsync();
        // P2 is gone; P1 and the spectator are on their loading screens.
        await Db.HashSetAsync(GatewayPresence.ConnectionKey(P1), "id", "c1");
        await Db.HashSetAsync(GatewayPresence.ConnectionKey(Spectator), "id", "c4");
        await Db.StringSetAsync($"player_lobby:{P1}", Id(50));
        await Db.StringSetAsync($"party_ready:{Id(50)}", "1");
        var callbacks = Callbacks();

        await callbacks.ReleaseAsync((await callbacks.RegisterAsync(Body()))!.Ready!);

        Assert.Empty(await SentAsync());
        Assert.False(await Db.KeyExistsAsync($"party_ready:{Id(50)}"));
        foreach (string player in new[] { P1, Spectator })
        {
            var banner = Js.Parse((string?)await Db.ListGetByIndexAsync($"dll_notifications:{player}", 0) ?? "null")!;
            Assert.Equal(("admin_banner", MatchLaunches.ClosedMessage), ((string?)banner["type"], (string?)banner["message"]));
        }

        Assert.False(await Db.KeyExistsAsync($"dll_notifications:{P2}"));
        var closes = (await Db.SortedSetRangeByScoreAsync(DelayedMessages.Key))
            .Select(m => Js.Parse(m.ToString()) as JsonObject)
            .Where(e => (string?)e?["channel"] == GatewayChannels.Disconnect && e!["message"]!.ToJsonString().Contains("00000000000000000016", StringComparison.Ordinal))
            .Select(e => Js.Stringify(e!["message"])).Order().ToList();
        Assert.Equal(
            [$$"""{"playerId":"{{P1}}","connectionId":"c1","code":1000,"reason":"match-cancelled"}""",
             $$"""{"playerId":"{{Spectator}}","connectionId":"c4","code":1000,"reason":"match-cancelled"}"""],
            closes);
    }

    [SkippableFact]
    public async Task WithoutAMatchPortThePlayersGetUdpPort()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS to run");
        await SeedAsync(withMatch: false);
        await ConnectedAsync();
        var callbacks = Callbacks();

        await callbacks.ReleaseAsync((await callbacks.RegisterAsync(Body()))!.Ready!);

        var sent = await SentAsync();
        Assert.Equal(3, sent.Count);
        Assert.All(sent, s => Assert.Equal(41234, s.Message["payload"]!["game_server_instance"]!["port"]!.GetValue<int>()));
    }

    [SkippableFact]
    public async Task AP2PMatchIsHeldUntilItsHostNodeIsReadyOrItsRelayWasAskedFor()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS to run");
        await SeedAsync(p2p: true);
        await ConnectedAsync();
        var callbacks = Callbacks();

        var held = await callbacks.RegisterAsync(Body());
        Assert.NotNull(held);
        Assert.Null(held.Ready);
        Assert.Empty(await SentAsync());

        await callbacks.P2PReadyAsync(Body());
        // The humans (spectators included), never the bots.
        Assert.Equal([P1, P2, Spectator], (await SentAsync()).Select(s => s.Player));

        await Db.StringSetAsync($"p2p_relay:{Match}", "1");
        Assert.NotNull((await callbacks.RegisterAsync(Body()))!.Ready);
    }

    [SkippableFact]
    public async Task P2PReadyForAMatchThatIsNotP2PIsIgnored()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS to run");
        await SeedAsync(p2p: false);
        await ConnectedAsync();

        await Callbacks().P2PReadyAsync(Body());

        Assert.Empty(await SentAsync());
    }

    [SkippableFact]
    public async Task TheFirstRelayRequestDeploysItAndEveryOneIsAnsweredWithItsAddress()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS to run");
        await SeedAsync(p2p: true);
        var launcher = new Launcher();
        var callbacks = Callbacks(launcher, settings: new RollbackSettings { UdpServerIp = "203.0.113.5", OnDemand = true });

        var first = await callbacks.P2PFailedAsync(Body());
        var second = await callbacks.P2PFailedAsync(Body());

        Assert.Equal("""{"host":"203.0.113.5","port":57003}""", first!.ToJsonString());
        Assert.Equal(first.ToJsonString(), second!.ToJsonString());
        Assert.Equal([(57003, Match)], launcher.Deployed);
        Assert.Equal("1", (string?)await Db.StringGetAsync($"p2p_relay:{Match}"));
        var ttl = await Db.KeyTimeToLiveAsync($"p2p_relay:{Match}");
        Assert.InRange(ttl!.Value.TotalMinutes, 19, 20);
    }

    [SkippableFact]
    public async Task ARelayRequestForAMatchThatIsNotP2PIsAnsweredWithNothing()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS to run");
        await SeedAsync(p2p: false);
        var launcher = new Launcher();

        Assert.Null(await Callbacks(launcher, settings: new RollbackSettings { OnDemand = true }).P2PFailedAsync(Body()));
        Assert.Empty(launcher.Deployed);
        Assert.False(await Db.KeyExistsAsync($"p2p_relay:{Match}"));
    }

    [SkippableFact]
    // Ended here (MatchEnd), with the config's players in order, bots and spectators included (the TS server published
    // match:end with the same list).
    public async Task TheMatchIsEndedHereForEveryPlayer()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS to run");
        await SeedAsync();
        var ender = new Ender();

        await Callbacks(matchEnd: ender).EndMatchAsync(Body(), "/ovs_end_match");

        Assert.Equal([(Match, $"{P1},{P2},{Bot},{Spectator}")], ender.Ended);
    }

    [SkippableFact]
    public async Task AHostNodesStartIsKeptTenMinutes()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS to run");
        await SeedAsync(p2p: true);

        await Callbacks().MatchStartedAsync(Body());
        await Callbacks().MatchStartedAsync(Body("another key"));

        Assert.Equal("1", (string?)await Db.StringGetAsync($"match_started:{Match}"));
        Assert.InRange((await Db.KeyTimeToLiveAsync($"match_started:{Match}"))!.Value.TotalMinutes, 9, 10);
    }

    [SkippableFact]
    public async Task TheMvsiRegistryHasEveryPlayerBotsIncludedUnsigned()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS to run");
        await SeedAsync(p2p: true);
        var signer = new Signer();

        var registration = await Callbacks(signer: signer).RegisterLegacyAsync(Body());

        Assert.Equal("""{"max_players":4,"match_duration":36000,"players":[{"player_index":0,"ip":"198.51.100.1","is_host":true},""" +
            """{"player_index":1,"ip":"198.51.100.2","is_host":false},{"player_index":3,"ip":"","is_host":false},{"player_index":8888,"ip":"198.51.100.4","is_host":false}]}""",
            Encoding.UTF8.GetString(registration!.Body));
        Assert.Null(registration.Signature);
        Assert.Empty(signer.Signed);
        // Never held, P2P or not; bots included, as there.
        Assert.Equal(4, registration.Ready!.PlayerIds.Count);
    }
}

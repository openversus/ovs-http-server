using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Matches;
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
    private readonly ConcurrentQueue<(string Channel, JsonObject Message)> _published = new();

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
        // Channels ignore the database: only this class's match counts.
        foreach (string channel in new[] { RollbackCallbacks.InstanceReadyChannel, RollbackCallbacks.EndOfMatchChannel })
        {
            await _redis.GetSubscriber().SubscribeAsync(RedisChannel.Literal(channel), (_, m) =>
            {
                if (m.ToString().Contains(Match, StringComparison.Ordinal))
                {
                    _published.Enqueue((channel, (JsonObject)JsonNode.Parse(m.ToString())!));
                }
            });
        }
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
    }

    private IDatabase Db => _redis!.GetDatabase();

    private RollbackCallbacks Callbacks(Launcher? launcher = null, Signer? signer = null, RollbackSettings? settings = null) =>
        new(new ServiceCollection().AddSingleton<IConnectionMultiplexer>(_redis!).BuildServiceProvider(), launcher ?? new Launcher(), signer ?? new Signer(),
            new TestOptions<RollbackSettings>(settings ?? new RollbackSettings { UdpServerIp = "203.0.113.5", UdpPort = 41234 }), NullLogger<RollbackCallbacks>.Instance);

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

    private async Task<List<(string Channel, JsonObject Message)>> PublishedAsync(int waitMs = 200)
    {
        await Task.Delay(waitMs);
        return [.. _published];
    }

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
    public async Task ThePlayersAreToldToConnectWithTheMatchsOwnRollbackPort()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS to run");
        await SeedAsync();
        var callbacks = Callbacks();

        await callbacks.ReleaseAsync((await callbacks.RegisterAsync(Body()))!.Ready!);

        var (channel, message) = Assert.Single(await PublishedAsync());
        Assert.Equal(RollbackCallbacks.InstanceReadyChannel, channel);
        Assert.Equal(["containerMatchId", "playerIds", "resultId", "rollbackPort"], message.Select(kv => kv.Key));
        Assert.Equal(Match, message["containerMatchId"]!.GetValue<string>());
        Assert.Equal($$"""["{{P1}}","{{P2}}","{{Spectator}}"]""", message["playerIds"]!.ToJsonString());
        Assert.Matches("^[0-9a-f]{24}$", message["resultId"]!.GetValue<string>());
        // match:{id}'s port (the TS redisGetGamePort), not the notification's.
        Assert.Equal(57005, message["rollbackPort"]!.GetValue<int>());
    }

    [SkippableFact]
    public async Task WithoutAMatchPortThePlayersGetUdpPort()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS to run");
        await SeedAsync(withMatch: false);
        var callbacks = Callbacks();

        await callbacks.ReleaseAsync((await callbacks.RegisterAsync(Body()))!.Ready!);

        Assert.Equal(41234, Assert.Single(await PublishedAsync()).Message["rollbackPort"]!.GetValue<int>());
    }

    [SkippableFact]
    public async Task AP2PMatchIsHeldUntilItsHostNodeIsReadyOrItsRelayWasAskedFor()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS to run");
        await SeedAsync(p2p: true);
        var callbacks = Callbacks();

        var held = await callbacks.RegisterAsync(Body());
        Assert.NotNull(held);
        Assert.Null(held.Ready);

        await callbacks.P2PReadyAsync(Body());
        var (_, ready) = Assert.Single(await PublishedAsync());
        // The humans (spectators included), never the bots.
        Assert.Equal($$"""["{{P1}}","{{P2}}","{{Spectator}}"]""", ready["playerIds"]!.ToJsonString());

        await Db.StringSetAsync($"p2p_relay:{Match}", "1");
        Assert.NotNull((await callbacks.RegisterAsync(Body()))!.Ready);
    }

    [SkippableFact]
    public async Task P2PReadyForAMatchThatIsNotP2PIsIgnored()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS to run");
        await SeedAsync(p2p: false);

        await Callbacks().P2PReadyAsync(Body());

        Assert.Empty(await PublishedAsync());
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
    public async Task TheMatchsEndIsPublishedForEveryPlayer()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS to run");
        await SeedAsync();

        await Callbacks().EndMatchAsync(Body(), "/ovs_end_match");

        var (channel, message) = Assert.Single(await PublishedAsync());
        Assert.Equal(RollbackCallbacks.EndOfMatchChannel, channel);
        Assert.Equal($$"""{"playersIds":["{{P1}}","{{P2}}","{{Bot}}","{{Spectator}}"],"matchId":"{{Match}}"}""", message.ToJsonString());
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

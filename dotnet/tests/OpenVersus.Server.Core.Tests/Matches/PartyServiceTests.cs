using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Cosmetics;
using OpenVersus.Server.Core.FunFacts;
using OpenVersus.Server.Core.Matches;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Tests.Matches;

/// <summary>
/// The party lobby routes (PartyService) on real Redis, database 15, and Mongo, a database of its own
/// (OVS_TEST_REDIS[_USER/_PW], OVS_TEST_MONGO). Parity with the TS server is tools/matches/party_diff.mjs; these hold
/// the behaviour that matters to players, and the places the port deliberately differs.
/// </summary>
[Collection(RedisTestDatabase.Name)]
public sealed class PartyServiceTests : IAsyncLifetime
{
    private const string TestMongoDb = "ovs_party_tests";
    private static readonly string? s_redis = Environment.GetEnvironmentVariable("OVS_TEST_REDIS");
    private static readonly string? s_mongo = Environment.GetEnvironmentVariable("OVS_TEST_MONGO");
    private IMongoClient? _mongo;
    private ConnectionMultiplexer? _redis;

    // Ids no real player has; every key a test makes holds one of them.
    private static readonly string Owner = "0000000000000000000e0001", Guest = "0000000000000000000e0002", Lobby = "0000000000000000000e0101";

    private sealed class Cosmetics : ICosmeticsService
    {
        public Task<JsonObject> EquippedAsync(string accountId, CancellationToken ct) => Task.FromResult(new JsonObject { ["Banner"] = "banner_default" });
        public Task<bool> EquipTauntAsync(string accountId, JsonNode? character, JsonNode? index, JsonNode? slug, CancellationToken ct) => Task.FromResult(true);
        public Task<bool> EquipStatTrackerAsync(string accountId, JsonNode? index, JsonNode? slug, CancellationToken ct) => Task.FromResult(true);
        public Task<bool> EquipAsync(CosmeticSlot slot, string accountId, JsonNode? slug, bool given, CancellationToken ct) => Task.FromResult(true);
        public Task<bool> SetProfileIconAsync(string accountId, JsonNode? slug, CancellationToken ct) => Task.FromResult(true);
        public Task WriteMatchCopyAsync(string accountId, JsonObject cosmetics) => Task.CompletedTask;
    }

    private sealed class NoFacts : IFunFacts
    {
        public Task<FunFact?> RandomAsync(string accountId, CancellationToken ct) => Task.FromResult<FunFact?>(null);
    }

    public async Task InitializeAsync()
    {
        if (string.IsNullOrEmpty(s_redis) || string.IsNullOrEmpty(s_mongo))
        {
            return;
        }

        _mongo = new MongoClient(s_mongo);
        await _mongo.DropDatabaseAsync(TestMongoDb);
        string[] parts = s_redis.Split(':');
        var options = new ConfigurationOptions { EndPoints = { { parts[0], parts.Length > 1 ? int.Parse(parts[1]) : 6379 } }, DefaultDatabase = 15 };
        options.User = Environment.GetEnvironmentVariable("OVS_TEST_REDIS_USER");
        options.Password = Environment.GetEnvironmentVariable("OVS_TEST_REDIS_PW");
        _redis = await ConnectionMultiplexer.ConnectAsync(options);
        await CleanAsync();
        PartyService.JoinNoticeDelay = TimeSpan.Zero;
        PartyService.LockNoticeDelay = TimeSpan.Zero;
    }

    public async Task DisposeAsync()
    {
        if (_redis is not null)
        {
            await CleanAsync();
            await _redis.DisposeAsync();
        }

        if (_mongo is not null)
        {
            await _mongo.DropDatabaseAsync(TestMongoDb);
        }
    }

    private async Task CleanAsync()
    {
        var server = _redis!.GetServer(_redis.GetEndPoints()[0]);
        foreach (var key in server.Keys(15, "*0000000000000000000e*"))
        {
            await _redis.GetDatabase().KeyDeleteAsync(key);
        }

        foreach (var key in server.Keys(15, "connections:198.51.100.*"))
        {
            await _redis.GetDatabase().KeyDeleteAsync(key);
        }
    }

    private IDatabase Db => _redis!.GetDatabase();

    private IPartyService Service()
    {
        var services = new ServiceCollection()
            .AddSingleton<IConnectionMultiplexer>(_redis!)
            .AddSingleton(_mongo!.GetDatabase(TestMongoDb))
            .BuildServiceProvider();
        return new PartyService(services, new Cosmetics(), new NoFacts(), new TestOptions<LobbySettings>(new LobbySettings()), TimeProvider.System, NullLogger<PartyService>.Instance);
    }

    private static PartyRequest Asking(string player, string body) => new(player, null, "198.51.100.7", (JsonObject)JsonNode.Parse(body)!);

    // What ws:send carries for these players (the channel is global: the messages are filtered to this test's ids).
    private async Task<ConcurrentQueue<JsonObject>> ListenAsync()
    {
        var heard = new ConcurrentQueue<JsonObject>();
        await _redis!.GetSubscriber().SubscribeAsync(RedisChannel.Literal("ws:send"), (_, message) =>
        {
            if (JsonNode.Parse(message.ToString()) is JsonObject sent && sent["playerIds"]!.ToJsonString().Contains("0000000000000000000e"))
            {
                heard.Enqueue(sent);
            }
        });
        return heard;
    }

    private async Task<string> LobbyJson(string id) => (await Db.StringGetAsync($"lobby:{id}")).ToString();

    private Task SeedLobbyAsync(params string[] players) => Db.StringSetAsync($"lobby:{Lobby}",
        Js.Stringify(new JsonObject { ["lobbyId"] = Lobby, ["ownerId"] = Owner, ["ownerUsername"] = "Owner", ["mode"] = "1v1", ["playerIds"] = new JsonArray(players.Select(p => (JsonNode?)p).ToArray()), ["createdAt"] = 1790000000123 }),
        TimeSpan.FromHours(1));

    private static async Task<T> Eventually<T>(Func<T?> read) where T : class
    {
        for (int i = 0; i < 50; i++)
        {
            if (read() is { } value)
            {
                return value;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("nothing arrived");
    }

    [Fact]
    public async Task ACustomLobbyRequestIsHandedOnAndAPartyOneIsNot()
    {
        if (_redis is null)
        {
            return;
        }

        await Db.StringSetAsync($"custom_lobby_ssc:{Lobby}", "{}");
        var party = Service();
        Assert.Equal(Lobby, await party.CustomLobbyAsync("lock_lobby_loadout", Asking(Owner, $$"""{"LobbyId": "{{Lobby}}"}""")));
        // An empty LobbyId is no id (JavaScript's ||): the MatchID is the lobby.
        Assert.Equal(Lobby, await party.CustomLobbyAsync("invite_to_player_lobby", Asking(Owner, $$"""{"LobbyId": "", "MatchID": "{{Lobby}}"}""")));
        Assert.Null(await party.CustomLobbyAsync("lock_lobby_loadout", Asking(Owner, """{"LobbyId": "0000000000000000000e0999"}""")));

        // create_party_lobby follows the player into their custom lobby only while it exists.
        await Db.StringSetAsync($"ssc_custom_lobby_player:{Owner}", Lobby);
        Assert.Equal(Lobby, await party.CustomLobbyAsync("create_party_lobby", Asking(Owner, "{}")));
        await Db.KeyDeleteAsync($"custom_lobby_ssc:{Lobby}");
        Assert.Null(await party.CustomLobbyAsync("create_party_lobby", Asking(Owner, "{}")));
        Assert.Equal(Lobby, await party.CustomLobbyAsync("leave_player_lobby", Asking(Owner, "{}")));
    }

    [Fact]
    public async Task AnInvitedPlayerJoinsAndTheOwnerIsToldOnce()
    {
        if (_redis is null)
        {
            return;
        }

        var heard = await ListenAsync();
        await SeedLobbyAsync(Owner);
        await Db.StringSetAsync($"pending_join_lobby:{Guest}", Lobby);
        var cancels = new ConcurrentQueue<string>();
        await _redis.GetSubscriber().SubscribeAsync(RedisChannel.Literal("matchmaking:cancel"), (_, m) =>
        {
            if (m.ToString().Contains(Guest))
            {
                cancels.Enqueue(m.ToString());
            }
        });

        var answer = await Service().JoinAsync(Asking(Guest, "{}"));

        Assert.Equal(Lobby, answer["body"]!["lobby"]!["MatchID"]!.GetValue<string>());
        Assert.Equal("2v2", answer["body"]!["lobby"]!["ModeString"]!.GetValue<string>());
        Assert.Contains(Guest, await LobbyJson(Lobby));
        Assert.False(await Db.KeyExistsAsync($"pending_join_lobby:{Guest}"));
        Assert.Equal(Lobby, (string?)await Db.StringGetAsync($"player_lobby:{Guest}"));

        var told = await Eventually(() => heard.TryPeek(out var m) ? m : null);
        Assert.Equal(Owner, told["playerIds"]!.AsArray().Single()!.GetValue<string>());
        Assert.Equal("PlayerJoinedLobby", told["message"]!["data"]!["template_id"]!.GetValue<string>());
        Assert.Equal(Guest, told["message"]!["data"]!["Player"]!["Account"]!["id"]!.GetValue<string>());
        await Eventually(() => cancels.TryPeek(out var c) ? c : null);
        // The owner's own pending join points back at the party, so their next join stays in it.
        await Eventually(async () => (string?)await Db.StringGetAsync($"pending_join_lobby:{Owner}"));

        // The owner joining their own lobby again tells nobody.
        heard.Clear();
        await Service().JoinAsync(Asking(Owner, $$"""{"LobbyId": "{{Lobby}}"}"""));
        await Task.Delay(200);
        Assert.Empty(heard);
    }

    private static async Task Eventually(Func<Task<string?>> read)
    {
        for (int i = 0; i < 50; i++)
        {
            if (await read() is not null)
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("nothing arrived");
    }

    [Fact]
    public async Task LeavingAPartyTellsTheRestAndGivesTheLeaverASoloLobby()
    {
        if (_redis is null)
        {
            return;
        }

        var heard = await ListenAsync();
        await SeedLobbyAsync(Owner, Guest);
        await Db.StringSetAsync($"player_lobby:{Guest}", Lobby);
        await Db.SetAddAsync($"party_ready:{Lobby}", Owner);

        var answer = await Service().LeaveAsync(Asking(Guest, $$"""{"LobbyId": "{{Lobby}}"}"""));

        string solo = answer["body"]!["lobby"]!["MatchID"]!.GetValue<string>();
        Assert.NotEqual(Lobby, solo);
        Assert.Equal(solo, (string?)await Db.StringGetAsync($"player_lobby:{Guest}"));
        Assert.DoesNotContain(Guest, await LobbyJson(Lobby));
        Assert.False(await Db.KeyExistsAsync($"party_ready:{Lobby}"));
        var told = await Eventually(() => heard.TryPeek(out var m) ? m : null);
        Assert.Equal("PlayerLeftLobby", told["message"]!["data"]!["template_id"]!.GetValue<string>());
        Assert.Equal(Owner, told["message"]!["data"]!["NewLeader"]!.GetValue<string>());
        // The TS server's key order for this one message.
        Assert.Equal(["data", "payload", "cmd", "header"], told["message"]!.AsObject().Select(e => e.Key));
    }

    [Fact]
    public async Task ARefusedLockIsAnsweredAndRecordsNothing()
    {
        if (_redis is null)
        {
            return;
        }

        await _mongo!.GetDatabase(TestMongoDb).GetCollection<BsonDocument>("playertesters").InsertOneAsync(new BsonDocument { ["_id"] = ObjectId.Parse(Owner), ["character"] = "character_shaggy" });
        await Db.HashSetAsync($"player:{Owner}", "character", "character_shaggy");

        var refused = await Service().LockLoadoutAsync(Asking(Owner, """{"Loadout": {"Character": "character_C022", "Skin": "x"}}"""));
        Assert.False(refused["body"]!["bAreAllLoadoutsLocked"]!.GetValue<bool>());
        Assert.Equal("character_shaggy", (string?)await Db.HashGetAsync($"player:{Owner}", "character"));

        var locked = await Service().LockLoadoutAsync(Asking(Owner, """{"Loadout": {"Character": "character_batman", "Skin": "skin_batman_default"}}"""));
        Assert.True(locked["body"]!["bAreAllLoadoutsLocked"]!.GetValue<bool>());
        Assert.Equal("character_batman", (string?)await Db.HashGetAsync($"player:{Owner}", "character"));
        Assert.Equal("198.51.100.7", (string?)await Db.HashGetAsync($"player:{Owner}", "ip"));
    }

    [Fact]
    public async Task AModeChangeKeepsTheSessionAndAnotherPlayersIpCopy()
    {
        if (_redis is null)
        {
            return;
        }

        string ip = "198.51.100.9";
        await Db.StringSetAsync($"player_lobby:{Owner}", Lobby);
        await Db.HashSetAsync($"player:{Owner}:lobby:{Lobby}", [new("id", Lobby), new("owner", Owner), new("mode", "1v1")]);
        await Db.HashSetAsync($"connections:{Owner}", [new("id", Owner), new("current_ip", ip), new("GameplayPreferences", "448")]);
        // The IP-keyed copy is another household member's.
        await Db.HashSetAsync($"connections:{ip}", [new("id", Guest), new("lobby_id", "theirs")]);

        await Service().SetModeAsync(Asking(Owner, """{"ModeString": "2v2"}"""));

        Assert.Equal(Lobby, (string?)await Db.HashGetAsync($"connections:{Owner}", "lobby_id"));
        Assert.Equal("448", (string?)await Db.HashGetAsync($"connections:{Owner}", "GameplayPreferences"));
        Assert.Equal("theirs", (string?)await Db.HashGetAsync($"connections:{ip}", "lobby_id"));
        Assert.Equal("2v2", (string?)await Db.HashGetAsync($"player:{Owner}:lobby:{Lobby}", "mode"));

        // The player's own copy is updated.
        await Db.HashSetAsync($"connections:{ip}", "id", Owner);
        await Db.HashDeleteAsync($"connections:{Owner}", "lobby_id");
        await Service().SetModeAsync(Asking(Owner, """{"ModeString": "1v1"}"""));
        Assert.Equal(Lobby, (string?)await Db.HashGetAsync($"connections:{ip}", "lobby_id"));
    }
}

using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Clients;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Cosmetics;
using OpenVersus.Server.Core.Identity;
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

    private sealed class Gate : IClientUpdateGate
    {
        public HashSet<string> Outdated { get; } = [];
        public List<string> Modals { get; } = [];

        public Task<IReadOnlyList<ClientUpdateState>> RequiringUpdateAsync(IEnumerable<string> playerIds) =>
            Task.FromResult<IReadOnlyList<ClientUpdateState>>(playerIds.Where(Outdated.Contains).Distinct().Select(p => new ClientUpdateState(p, "", false, true)).ToList());

        public Task<IReadOnlyList<bool>> RequestModalsAsync(IEnumerable<string> playerIds)
        {
            Modals.AddRange(playerIds);
            return Task.FromResult<IReadOnlyList<bool>>([]);
        }

        public Task<ClientUpdateState> ForRequestAsync(AccountLookup lookup, JsonObject? claims) => throw new NotSupportedException();
        public Task<double> ModalNonceAsync(string playerId) => throw new NotSupportedException();
        public JsonObject FailureBody() => new() { ["body"] = new JsonObject { ["error"] = "client_update_required" }, ["metadata"] = null, ["return_code"] = 1 };
    }

    private readonly Gate _gate = new();

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
        return new PartyService(services, new Cosmetics(), new NoFacts(), _gate, new TestOptions<LobbySettings>(new LobbySettings()), TimeProvider.System, NullLogger<PartyService>.Instance);
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

    // The game readies its party lobby before its matchmaking request, and backs out of a refused ready (not of a refused
    // request): a party with anyone who must update cannot ready, whoever presses it; un-readying always works.
    [Fact]
    public async Task AReadyFromAPartyWithAnOutdatedClientIsRefusedAndTheyAreToasted()
    {
        if (_redis is null)
        {
            return;
        }

        await SeedLobbyAsync(Owner, Guest);
        var heard = await ListenAsync();
        _gate.Outdated.Add(Guest);
        var party = Service();

        var refused = await party.SetReadyAsync(Asking(Owner, $$"""{"MatchID": "{{Lobby}}", "Ready": true}"""));
        Assert.Equal(1, (int)refused["return_code"]!);
        Assert.Equal("client_update_required", (string?)refused["body"]!["error"]);
        Assert.Equal([Guest], _gate.Modals);
        Assert.False(await Db.KeyExistsAsync($"party_ready:{Lobby}"));

        var unready = await party.SetReadyAsync(Asking(Guest, $$"""{"MatchID": "{{Lobby}}", "Ready": false}"""));
        Assert.Equal(0, (int)unready["return_code"]!);
        Assert.Equal([Guest], _gate.Modals);

        _gate.Outdated.Clear();
        var ready = await party.SetReadyAsync(Asking(Owner, $$"""{"MatchID": "{{Lobby}}", "Ready": true}"""));
        Assert.Equal(0, (int)ready["return_code"]!);
        Assert.Equal([Owner], (await Db.SetMembersAsync($"party_ready:{Lobby}")).Select(v => v.ToString()));
        // Only the ready that went through told the other member that someone readied.
        await Eventually(() => heard.FirstOrDefault(h => (bool?)h["message"]!["data"]!["Ready"] == true));
        await Task.Delay(200);
        Assert.Equal(1, heard.Count(h => (bool?)h["message"]!["data"]!["Ready"] == true));

        // A rift lobby's ready goes through: its start_rift_node is the gate.
        await Db.KeyDeleteAsync($"party_ready:{Lobby}");
        await Db.StringSetAsync($"lobby:{Lobby}", Js.Stringify(new JsonObject { ["lobbyId"] = Lobby, ["ownerId"] = Guest, ["mode"] = "rift_lobby", ["playerIds"] = new JsonArray(Guest) }));
        _gate.Outdated.Add(Guest);
        var rift = await party.SetReadyAsync(Asking(Guest, $$"""{"MatchID": "{{Lobby}}", "Ready": true}"""));
        Assert.Equal(0, (int)rift["return_code"]!);
        Assert.Equal([Guest], _gate.Modals);
    }

    // Each player's ready is their own: an un-ready takes back only the player who sent it, and the party is all ready
    // again as soon as that player readies again.
    [Fact]
    public async Task AnUnreadyTakesBackOnlyThatPlayersReady()
    {
        if (_redis is null)
        {
            return;
        }

        await SeedLobbyAsync(Owner, Guest);
        var party = Service();

        await party.SetReadyAsync(Asking(Owner, $$"""{"MatchID": "{{Lobby}}", "Ready": true}"""));
        var both = await party.SetReadyAsync(Asking(Guest, $$"""{"MatchID": "{{Lobby}}", "Ready": true}"""));
        Assert.True((bool)both["body"]!["bAllPlayersReady"]!);

        var unready = await party.SetReadyAsync(Asking(Guest, $$"""{"MatchID": "{{Lobby}}", "Ready": false}"""));
        Assert.False((bool)unready["body"]!["bAllPlayersReady"]!);
        Assert.Equal([Owner], (await Db.SetMembersAsync($"party_ready:{Lobby}")).Select(v => v.ToString()));

        var again = await party.SetReadyAsync(Asking(Guest, $$"""{"MatchID": "{{Lobby}}", "Ready": true}"""));
        Assert.True((bool)again["body"]!["bAllPlayersReady"]!);
    }

    // A party lobby of two players that is searching: either player's un-ready cancels the search for both (their
    // tickets, each game told) and un-readies both. A searching solo player has no un-ready, only Cancel: the cancel
    // route (MatchmakingRequestTests.ACancelReachesTheWholeLobbyAndUnreadiesIt).
    [Fact]
    public async Task AnUnreadyWhileThePartySearchesCancelsTheSearchAndUnreadiesEveryone()
    {
        if (_redis is null)
        {
            return;
        }

        string list = $"test_queue:{Lobby}";
        string ticket = Js.Stringify(new JsonObject
        {
            ["matchType"] = list,
            ["matchmakingRequestId"] = "request-e1",
            ["players"] = new JsonArray(new JsonObject { ["id"] = Owner }, new JsonObject { ["id"] = Guest }),
        });
        try
        {
            await SeedLobbyAsync(Owner, Guest);
            await Db.SetAddAsync($"party_ready:{Lobby}", [Owner, Guest]);
            await Db.ListRightPushAsync(list, ticket);
            await Db.HashSetAsync(MatchmakingQueue.QueuedKey, [new(Owner, ticket), new(Guest, ticket)]);
            var heard = await ListenAsync();

            var unready = await Service().SetReadyAsync(Asking(Guest, $$"""{"MatchID": "{{Lobby}}", "Ready": false}"""));

            Assert.False((bool)unready["body"]!["bAllPlayersReady"]!);
            Assert.False(await Db.KeyExistsAsync($"party_ready:{Lobby}"));
            Assert.Equal(0, await Db.ListLengthAsync(list));
            Assert.False(await Db.HashExistsAsync(MatchmakingQueue.QueuedKey, Owner));
            Assert.False(await Db.HashExistsAsync(MatchmakingQueue.QueuedKey, Guest));
            // Each game hears its search cancelled, with the request's id.
            foreach (string player in new[] { Owner, Guest })
            {
                await Eventually(() => heard.FirstOrDefault(h => (string?)h["playerIds"]![0] == player && (string?)h["message"]!["cmd"] == "matchmaking-cancel"
                    && (string?)h["message"]!["payload"]!["id"] == "request-e1"));
            }
        }
        finally
        {
            await Db.HashDeleteAsync(MatchmakingQueue.QueuedKey, [Owner, Guest]);
        }
    }

    // Training mode and matchmaking mark the lobby not joinable, and joinable again after: the flag is kept, and a party's
    // lobby keeps its 8 hours (one in training mode for over an hour lost its lobby when this wrote 1 h).
    [Fact]
    public async Task NotJoinableAndJoinableAgainKeepThePartysLobbyAndItsLifetime()
    {
        if (_redis is null)
        {
            return;
        }

        await SeedLobbyAsync(Owner, Guest);
        var party = Service();

        await party.SetNotJoinableAsync(Asking(Owner, $$"""{"LobbyId": "{{Lobby}}"}"""));
        Assert.False((bool)JsonNode.Parse(await LobbyJson(Lobby))!["joinable"]!);
        Assert.InRange((await Db.KeyTimeToLiveAsync($"lobby:{Lobby}"))!.Value, TimeSpan.FromHours(7.9), TimeSpan.FromHours(8));

        await party.SetJoinableAsync(Asking(Owner, $$"""{"LobbyId": "{{Lobby}}"}"""));
        Assert.True((bool)JsonNode.Parse(await LobbyJson(Lobby))!["joinable"]!);
        Assert.Equal([Owner, Guest], JsonNode.Parse(await LobbyJson(Lobby))!["playerIds"]!.AsArray().Select(p => (string)p!));
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
        // The owner is searching: a player joining the party takes the party out of the queue ("party-changed").
        string ticket = $$"""{"matchType":"2v2","players":[{"id":"{{Owner}}"}],"matchmakingRequestId":"party-test"}""";
        await Db.ListRightPushAsync("2v2", ticket);
        await Db.HashSetAsync(MatchmakingQueue.QueuedKey, Owner, ticket);

        var answer = await Service().JoinAsync(Asking(Guest, "{}"));

        Assert.Equal(Lobby, answer["body"]!["lobby"]!["MatchID"]!.GetValue<string>());
        Assert.Equal("2v2", answer["body"]!["lobby"]!["ModeString"]!.GetValue<string>());
        Assert.Contains(Guest, await LobbyJson(Lobby));
        Assert.False(await Db.KeyExistsAsync($"pending_join_lobby:{Guest}"));
        Assert.Equal(Lobby, (string?)await Db.StringGetAsync($"player_lobby:{Guest}"));

        var told = await Eventually(() => heard.FirstOrDefault(m => (string?)m["message"]!["data"]?["template_id"] == "PlayerJoinedLobby"));
        Assert.Equal(Owner, told["playerIds"]!.AsArray().Single()!.GetValue<string>());
        Assert.Equal(Guest, told["message"]!["data"]!["Player"]!["Account"]!["id"]!.GetValue<string>());
        // Taken out of the queue first, and told so.
        var canceled = Assert.Single(heard, m => (string?)m["message"]!["cmd"] == "matchmaking-cancel");
        Assert.Equal(Owner, canceled["playerIds"]!.AsArray().Single()!.GetValue<string>());
        Assert.False(await Db.HashExistsAsync(MatchmakingQueue.QueuedKey, Owner));
        Assert.DoesNotContain(ticket, (await Db.ListRangeAsync("2v2")).Select(v => v.ToString()));
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
    public async Task AnInviteIsSentOnlyWhenItNamesALobby()
    {
        if (_redis is null)
        {
            return;
        }

        var heard = await ListenAsync();
        await Service().InviteAsync(Asking(Owner, $$"""{"InviteeAccountID": "{{Guest}}"}"""));
        await Task.Delay(200);
        Assert.Empty(heard);
        Assert.False(await Db.KeyExistsAsync($"pending_join_lobby:{Guest}"));

        await SeedLobbyAsync(Owner);
        await Service().InviteAsync(Asking(Owner, $$"""{"InviteeAccountID": "{{Guest}}", "LobbyId": "{{Lobby}}"}"""));
        var told = await Eventually(() => heard.TryPeek(out var m) ? m : null);
        Assert.Equal(Guest, told["playerIds"]!.AsArray().Single()!.GetValue<string>());
        Assert.Equal("InviteReceivedForLobby", told["message"]!["data"]!["template_id"]!.GetValue<string>());
        Assert.Equal(Lobby, (string?)await Db.StringGetAsync($"pending_join_lobby:{Guest}"));
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
    // The solo lobby shows the fighter the leaver's lobby had (player:{id}, written at creation and by every lock), not
    // Shaggy: their session has no fighter until a matchmaking request writes one (the TS server read the session only).
    public async Task ALeaverWhoHasNotQueuedKeepsTheirFighter()
    {
        if (_redis is null)
        {
            return;
        }

        await SeedLobbyAsync(Owner, Guest);
        await Db.StringSetAsync($"player_lobby:{Guest}", Lobby);
        await Db.HashSetAsync($"connections:{Guest}", [new("id", Guest), new("GameplayPreferences", "448")]);
        await Db.HashSetAsync($"player:{Guest}", [new("character", "character_Jason"), new("skin", "skin_jason_000")]);

        var answer = await Service().LeaveAsync(Asking(Guest, $$"""{"LobbyId": "{{Lobby}}"}"""));

        var loadout = answer["body"]!["lobby"]!["LockedLoadouts"]![Guest]!;
        Assert.Equal(("character_Jason", "skin_jason_000"), (loadout["Character"]!.GetValue<string>(), loadout["Skin"]!.GetValue<string>()));
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
    public async Task AModeChangeReachesThePartyAndKeepsTheSessionAndAnotherPlayersIpCopy()
    {
        if (_redis is null)
        {
            return;
        }

        string ip = "198.51.100.9";
        await Db.StringSetAsync($"player_lobby:{Owner}", Lobby);
        await Db.HashSetAsync($"connections:{Owner}", [new("id", Owner), new("current_ip", ip), new("GameplayPreferences", "448")]);
        // The IP-keyed copy is another household member's.
        await Db.HashSetAsync($"connections:{ip}", [new("id", Guest), new("lobby_id", "theirs")]);

        var heard = await ListenAsync();
        await SeedLobbyAsync(Owner, Guest);
        await Service().SetModeAsync(Asking(Owner, """{"ModeString": "2v2"}"""));

        // Everyone in the party hears of it.
        var told = await Eventually(() => heard.TryPeek(out var m) ? m : null);
        Assert.Equal([Owner, Guest], told["playerIds"]!.AsArray().Select(p => p!.GetValue<string>()));
        Assert.Equal("OnLobbyModeUpdated", told["message"]!["data"]!["template_id"]!.GetValue<string>());
        Assert.Equal(Lobby, (string?)await Db.HashGetAsync($"connections:{Owner}", "lobby_id"));
        Assert.Equal("448", (string?)await Db.HashGetAsync($"connections:{Owner}", "GameplayPreferences"));
        Assert.Equal("theirs", (string?)await Db.HashGetAsync($"connections:{ip}", "lobby_id"));
        // The party lobby keeps the mode (the lobby a join sends shows it); no record of the TS createLobby is written.
        Assert.Equal("2v2", (string?)JsonNode.Parse(await LobbyJson(Lobby))!["mode"]);
        Assert.False(await Db.KeyExistsAsync($"player:{Owner}:lobby:{Lobby}"));

        // The player's own copy is updated.
        await Db.HashSetAsync($"connections:{ip}", "id", Owner);
        await Db.HashDeleteAsync($"connections:{Owner}", "lobby_id");
        await Service().SetModeAsync(Asking(Owner, """{"ModeString": "1v1"}"""));
        Assert.Equal(Lobby, (string?)await Db.HashGetAsync($"connections:{ip}", "lobby_id"));
    }

    // Only the lobby's owner changes its mode; a rift or Arena lobby's mode is its kind and stays.
    [Fact]
    public async Task OnlyTheOwnerChangesTheModeAndAnArenaLobbyStaysOne()
    {
        if (_redis is null)
        {
            return;
        }

        await SeedLobbyAsync(Owner, Guest);
        await Db.StringSetAsync($"player_lobby:{Guest}", Lobby);
        await Service().SetModeAsync(Asking(Guest, """{"ModeString": "FFA"}"""));
        Assert.Equal("1v1", (string?)JsonNode.Parse(await LobbyJson(Lobby))!["mode"]);

        await Db.StringSetAsync($"lobby:{Lobby}", Js.Stringify(new JsonObject { ["lobbyId"] = Lobby, ["ownerId"] = Owner, ["mode"] = "arena_lobby", ["playerIds"] = new JsonArray(Owner) }));
        await Db.StringSetAsync($"player_lobby:{Owner}", Lobby);
        await Service().SetModeAsync(Asking(Owner, """{"ModeString": "1v1"}"""));
        Assert.Equal("arena_lobby", (string?)JsonNode.Parse(await LobbyJson(Lobby))!["mode"]);
    }

    [Fact]
    // The TS close (websocket.ts 422-509): with the owner's game gone the lobby goes; the other is told, gets a solo lobby
    // and is sent to it (pending_join_lobby). Unlike a leave, no party_left notice.
    public async Task WhenTheOwnersGameIsGoneThePartyEndsAndTheOtherIsGivenALobby()
    {
        if (_redis is null)
        {
            return;
        }

        var heard = await ListenAsync();
        await SeedLobbyAsync(Owner, Guest);
        await Db.StringSetAsync($"player_lobby:{Owner}", Lobby);
        await Db.StringSetAsync($"player_lobby:{Guest}", Lobby);
        await Db.SetAddAsync($"party_ready:{Lobby}", Guest);

        await Service().PlayerDisconnectedAsync(Owner);

        Assert.False(await Db.KeyExistsAsync($"lobby:{Lobby}"));
        Assert.False(await Db.KeyExistsAsync($"player_lobby:{Owner}"));
        Assert.False(await Db.KeyExistsAsync($"party_ready:{Lobby}"));
        string solo = (string?)await Db.StringGetAsync($"pending_join_lobby:{Guest}") ?? "";
        try
        {
            Assert.NotEqual("", solo);
            Assert.NotEqual(Lobby, solo);
            Assert.Equal(solo, (string?)await Db.StringGetAsync($"player_lobby:{Guest}"));
            Assert.Contains(Guest, await LobbyJson(solo));
            var told = await Eventually(() => heard.TryPeek(out var m) ? m : null);
            Assert.Equal(Guest, told["playerIds"]!.AsArray().Single()!.GetValue<string>());
            Assert.Equal("PlayerLeftLobby", told["message"]!["data"]!["template_id"]!.GetValue<string>());
            Assert.Equal(Owner, told["message"]!["data"]!["Player"]!["Account"]!["id"]!.GetValue<string>());
            Assert.Equal(Guest, told["message"]!["data"]!["NewLeader"]!.GetValue<string>());
            Assert.Equal(["data", "payload", "cmd", "header"], told["message"]!.AsObject().Select(e => e.Key));
            Assert.False(await Db.KeyExistsAsync($"dll_notifications:{Guest}"));
        }
        finally
        {
            await Db.KeyDeleteAsync($"lobby:{solo}");
        }
    }

    [Fact]
    // The other's game gone: the owner keeps the lobby, alone, and is sent back to it.
    public async Task WhenTheOthersGameIsGoneTheOwnerKeepsTheLobbyAlone()
    {
        if (_redis is null)
        {
            return;
        }

        var heard = await ListenAsync();
        await SeedLobbyAsync(Owner, Guest);
        await Db.StringSetAsync($"player_lobby:{Owner}", Lobby);
        await Db.StringSetAsync($"player_lobby:{Guest}", Lobby);

        await Service().PlayerDisconnectedAsync(Guest);

        var lobby = (JsonObject)JsonNode.Parse(await LobbyJson(Lobby))!;
        Assert.Equal([Owner], lobby["playerIds"]!.AsArray().Select(p => p!.GetValue<string>()));
        Assert.Equal("1v1", lobby["mode"]!.GetValue<string>());
        Assert.False(await Db.KeyExistsAsync($"player_lobby:{Guest}"));
        Assert.Equal(Lobby, (string?)await Db.StringGetAsync($"player_lobby:{Owner}"));
        Assert.Equal(Lobby, (string?)await Db.StringGetAsync($"pending_join_lobby:{Owner}"));
        var told = await Eventually(() => heard.TryPeek(out var m) ? m : null);
        Assert.Equal(Owner, told["playerIds"]!.AsArray().Single()!.GetValue<string>());
        Assert.Equal(Owner, told["message"]!["data"]!["NewLeader"]!.GetValue<string>());
    }

    [Fact]
    // Both games gone (both closed in a post-match window, say, handled one after the other when it ends), in either
    // order: nothing of the party is left.
    public async Task WhenBothGamesAreGoneNothingOfThePartyIsLeft()
    {
        if (_redis is null)
        {
            return;
        }

        foreach (var (first, second) in new[] { (Owner, Guest), (Guest, Owner) })
        {
            await SeedLobbyAsync(Owner, Guest);
            await Db.StringSetAsync($"player_lobby:{Owner}", Lobby);
            await Db.StringSetAsync($"player_lobby:{Guest}", Lobby);
            var party = Service();

            await party.PlayerDisconnectedAsync(first);
            await party.ForgetLobbyAsync(first);
            string? solo = await Db.StringGetAsync($"player_lobby:{second}");
            await party.PlayerDisconnectedAsync(second);
            await party.ForgetLobbyAsync(second);

            Assert.False(await Db.KeyExistsAsync($"lobby:{Lobby}"));
            Assert.False(await Db.KeyExistsAsync($"player_lobby:{first}"));
            Assert.False(await Db.KeyExistsAsync($"player_lobby:{second}"));
            Assert.False(await Db.KeyExistsAsync($"lobby:{solo}"));
        }
    }
}

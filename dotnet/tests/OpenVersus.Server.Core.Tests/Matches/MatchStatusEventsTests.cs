using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Leaderboards;
using OpenVersus.Server.Core.Matches;
using OpenVersus.Server.Core.Realtime;
using OpenVersus.Server.Core.Seasons;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Tests.Matches;

/// <summary>
/// A rollback server's match status events (<see cref="IMatchStatusEvents"/>): the key, the match's flags, and a
/// player's disconnect (a rollback crash, mid-game, a pregame dodge), with where the port deliberately differs from the
/// TS server (spectators, the rating rule). Parity over whole scenarios is tools/matches/callbacks_diff.mjs. Real Redis,
/// database 15 (OVS_TEST_REDIS), and Mongo (a database of its own, dropped: OVS_TEST_MONGO).
/// </summary>
[Collection(RedisTestDatabase.Name)]
public sealed class MatchStatusEventsTests : IAsyncLifetime
{
    private static readonly string? s_redis = Environment.GetEnvironmentVariable("OVS_TEST_REDIS");
    private static readonly string? s_mongo = Environment.GetEnvironmentVariable("OVS_TEST_MONGO");
    private const string TestMongoDb = "ovs_match_status_tests";
    private const string UpdateKey = "Test-Match-Update-Key";
    private static string Id(int n) => $"00000000000000000017{n:D4}";
    private static readonly string Match = Id(100), Set = Id(101), P1 = Id(1), P2 = Id(2), Spectator = Id(4);

    private ConnectionMultiplexer? _redis;
    private IMongoClient? _mongo;
    private readonly ConcurrentQueue<(string Channel, JsonObject Message)> _published = new();

    private static bool Configured => !string.IsNullOrEmpty(s_redis) && !string.IsNullOrEmpty(s_mongo);

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
        _mongo = new MongoClient(s_mongo);
        await _mongo.DropDatabaseAsync(TestMongoDb);
        await CleanAsync();
        // Channels ignore the database: only this class's players' rank updates count.
        await _redis.GetSubscriber().SubscribeAsync(RedisChannel.Literal(ProfileNotifications.WsSendChannel), (_, m) =>
        {
            if (m.ToString().Contains("00000000000000000017", StringComparison.Ordinal) && m.ToString().Contains("\"FullRankUpdate\"", StringComparison.Ordinal))
            {
                _published.Enqueue((ProfileNotifications.WsSendChannel, (JsonObject)JsonNode.Parse(m.ToString())!));
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

        if (_mongo is not null)
        {
            await _mongo.DropDatabaseAsync(TestMongoDb);
        }
    }

    private async Task CleanAsync()
    {
        var server = _redis!.GetServer(_redis.GetEndPoints()[0]);
        foreach (var key in server.Keys(15, "*00000000000000000017*"))
        {
            await Db.KeyDeleteAsync(key);
        }

        await Db.KeyDeleteAsync("online_players");
    }

    private IDatabase Db => _redis!.GetDatabase();
    private IMongoDatabase Mongo => _mongo!.GetDatabase(TestMongoDb);

    private MatchStatusEvents Events(string matchUpdateKey = UpdateKey)
    {
        var services = new ServiceCollection().AddSingleton<IConnectionMultiplexer>(_redis!).AddSingleton(Mongo).BuildServiceProvider();
        var ranked = new TestOptions<RankedSettings>(new RankedSettings());
        var ratings = new SetRatings(services, new EloRatings(services, ranked, TimeProvider.System, NullLogger<EloRatings>.Instance), ranked,
            TimeProvider.System, NullLogger<SetRatings>.Instance);
        return new MatchStatusEvents(services, ratings, new EloRatings(services, ranked, TimeProvider.System, NullLogger<EloRatings>.Instance),
            new TestOptions<RollbackSettings>(new RollbackSettings { MatchUpdateKey = matchUpdateKey }),
            new TestOptions<SeasonSettings>(new SeasonSettings { Current = "Season:SeasonSix" }), TimeProvider.System, NullLogger<MatchStatusEvents>.Instance);
    }

    private static JsonArray OneVOne() =>
    [
        new JsonObject { ["playerId"] = P1, ["playerIndex"] = 0, ["teamIndex"] = 0, ["isHost"] = true, ["ip"] = "198.51.100.1", ["isBot"] = false },
        new JsonObject { ["playerId"] = P2, ["playerIndex"] = 1, ["teamIndex"] = 1, ["isHost"] = false, ["ip"] = "198.51.100.2", ["isBot"] = false },
    ];

    private static JsonArray WithSpectator()
    {
        var players = OneVOne();
        players.Add(new JsonObject { ["playerId"] = Spectator, ["playerIndex"] = 8888, ["teamIndex"] = -1, ["isHost"] = false, ["ip"] = "198.51.100.4", ["isSpectator"] = true });
        return players;
    }

    private async Task SeedAsync(JsonArray? players = null, JsonObject? config = null, JsonObject? match = null)
    {
        var notification = new JsonObject { ["players"] = players ?? OneVOne(), ["matchId"] = Match, ["matchKey"] = "k", ["mode"] = "1v1", ["rollbackPort"] = 57003 };
        foreach (var (key, value) in config ?? [])
        {
            notification[key] = value?.DeepClone();
        }

        var stored = new JsonObject { ["matchId"] = Match, ["status"] = "pending" };
        foreach (var (key, value) in match ?? [])
        {
            stored[key] = value?.DeepClone();
        }

        await Db.StringSetAsync(Match, Js.Stringify(notification));
        await Db.StringSetAsync($"match:{Match}", Js.Stringify(stored));
        foreach (string id in new[] { P1, P2 })
        {
            await Db.HashSetAsync($"connections:{id}", [new HashEntry("username", $"name {id[^1]}"), new HashEntry("character", id == P1 ? "character_jake" : "character_finn")]);
            // The player's record, as their lobby left it (create_party_lobby, the loadout lock).
            await Db.HashSetAsync($"player:{id}", [new HashEntry("character", id == P1 ? "character_jake" : "character_finn"), new HashEntry("skin", "skin_default")]);
        }
    }

    // P1 and P2's set after game 1, with RankedSets's pointer to its current game.
    private async Task SeedSetAsync()
    {
        await Db.StringSetAsync($"ranked_set:{Set}", Js.Stringify(new JsonObject { ["players"] = OneVOne(), ["mode"] = "1v1", ["gamesPlayed"] = 1, ["scores"] = new JsonArray(1, 0) }));
        await Db.SetAddAsync($"ranked_set_checkins:{Set}", P1);
        await Db.StringSetAsync($"ranked_set_match:{Set}", Match);
        await Db.StringSetAsync($"match_to_set:{Match}", Set);
        foreach (string id in new[] { P1, P2 })
        {
            await Db.StringSetAsync($"player_ranked_set:{id}", Set);
        }
    }

    private static JsonObject Event(string name, string? playerId = null) => new()
    {
        ["Timestamp"] = new JsonObject { ["Year"] = 2026 },
        ["Event"] = name,
        ["Description"] = name,
        ["matchId"] = Match,
        ["key"] = "k",
        ["NumPlayers"] = playerId is null ? 0 : 1,
        ["PlayerId"] = playerId ?? "",
        ["PlayerIds"] = playerId is null ? new JsonArray() : new JsonArray(playerId),
    };

    private async Task<int> SendAsync(JsonObject status, string? key = UpdateKey) => (await Events().HandleAsync(key, status, "198.51.100.200")).Status;

    private async Task<List<JsonObject>> NotificationsAsync(string playerId) =>
        [.. (await Db.ListRangeAsync($"dll_notifications:{playerId}")).Select(v => (JsonObject)JsonNode.Parse(v.ToString())!)];

    private async Task<long> RatingsAsync() => await Mongo.GetCollection<BsonDocument>("eloratings").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty);

    private async Task AssertSetDroppedAsync()
    {
        foreach (string key in new[] { $"player_ranked_set:{P1}", $"player_ranked_set:{P2}", $"ranked_set:{Set}", $"ranked_set_checkins:{Set}", $"ranked_set_match:{Set}" })
        {
            Assert.False(await Db.KeyExistsAsync(key), key);
        }
    }

    [SkippableFact]
    public async Task TheKeyIsCheckedAsTheTsServerDidButAnUnsetKeyNeverMatches()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        await SeedAsync();

        Assert.Equal(403, await SendAsync(Event("MatchStarted"), key: null));
        Assert.Equal(403, await SendAsync(Event("MatchStarted"), key: "Test-Match-Update-Kez"));
        Assert.False(await Db.KeyExistsAsync($"match_started:{Match}"));
        Assert.Equal(200, await SendAsync(Event("MatchStarted"), key: UpdateKey.ToUpperInvariant()));
        Assert.True(await Db.KeyExistsAsync($"match_started:{Match}"));

        var (status, answer) = await Events(MatchUpdateKeys.Placeholder).HandleAsync(MatchUpdateKeys.Placeholder, Event("MatchStarted"), null);
        Assert.Equal(403, status);
        Assert.Equal("""{"error":"Invalid signature"}""", answer.ToJsonString());
    }

    [SkippableFact]
    public async Task ACrashIsOnlyACrashAfterTheStartAndBeforeAResult()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        await SeedAsync();

        await SendAsync(Event("TerminatingError"));
        Assert.False(await Db.KeyExistsAsync($"match_server_crash:{Match}"));

        await SendAsync(Event("MatchStarted"));
        await Db.StringSetAsync($"game_result_received:{Match}", "1");
        await SendAsync(Event("AllPlayersDisconnected"));
        Assert.False(await Db.KeyExistsAsync($"match_server_crash:{Match}"));

        await SendAsync(Event("TerminatingError"));
        Assert.True(await Db.KeyExistsAsync($"match_server_crash:{Match}"));

        await SendAsync(Event("MatchEnded"));
        Assert.True(await Db.KeyExistsAsync($"match_ended:{Match}"));
        Assert.False(await Db.KeyExistsAsync($"match_started:{Match}"));
    }

    // The config a player's game was sent last, as the match flow keeps it (GameplayConfigs), until the match's end.
    private Task SentConfigAsync(string player) => Db.StringSetAsync($"match_config:{player}", Js.Stringify(new JsonObject
    {
        ["data"] = new JsonObject { ["MatchId"] = Match, ["GameplayConfig"] = new JsonObject { ["MatchId"] = Match }, ["template_id"] = "OnGameplayConfigNotified" },
        ["payload"] = new JsonObject(),
        ["header"] = "",
        ["cmd"] = "update",
    }));

    [SkippableFact]
    // A game that closed its websocket before the start dodged (the TS websocket's close): never a rollback crash, even
    // while the player is still listed online (the gateway keeps them through a post-match window).
    public async Task AGameClosedBeforeTheStartIsADodge()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        await SeedAsync();
        await SeedSetAsync();
        await SentConfigAsync(P2);
        await Db.SetAddAsync("online_players", [P1, P2]);

        await Events().GameClosedAsync(P2);

        Assert.False(await Db.KeyExistsAsync($"match_server_crash:{Match}"));
        Assert.Equal("pregame_dodge", (string?)await Db.StringGetAsync($"elo_processed_set:{Set}"));
        var ratings = Mongo.GetCollection<BsonDocument>("eloratings");
        Assert.Equal(1, (await ratings.Find(new BsonDocument("account_id", P1)).FirstAsync())["wins_1v1"].ToInt32());
        await AssertSetDroppedAsync();
        Assert.Equal(Set, (string?)await Db.StringGetAsync($"ranked_disconnect:{P2}"));
        Assert.Equal("Opponent left the match", Assert.Single(await NotificationsAsync(P1))["message"]!.GetValue<string>());
    }

    [SkippableFact]
    // The dodger's own disconnect cleans up their keys too, in another service, before or after the dodge: being set idle
    // must not make them a record holding a status alone.
    public async Task ADodgerWhoseKeysAreGoneIsNotGivenARecord()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        await SeedAsync();
        await SentConfigAsync(P2);
        await Db.KeyDeleteAsync($"player:{P2}");

        await Events().GameClosedAsync(P2);

        Assert.Equal("pregame_dodge", (string?)await Db.StringGetAsync($"elo_processed_set:{Match}"));
        Assert.False(await Db.KeyExistsAsync($"player:{P2}"));
        Assert.Equal("idle", (string?)await Db.HashGetAsync($"player:{P1}", "status"));
    }

    [SkippableFact]
    // Mid-game, the flag names the set for as long as the game can still run (TS's second write left it two minutes).
    public async Task AGameClosedMidGameFlagsItsSetUntilTheGameCanHaveEnded()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        await SeedAsync();
        await SeedSetAsync();
        await SentConfigAsync(P2);
        await Db.StringSetAsync($"match_started:{Match}", "1");

        await Events().GameClosedAsync(P2);

        Assert.Equal(Set, (string?)await Db.StringGetAsync($"ranked_disconnect:{P2}"));
        Assert.InRange((await Db.KeyTimeToLiveAsync($"ranked_disconnect:{P2}"))!.Value.TotalMinutes, 9, 10);
        Assert.Equal(0, await RatingsAsync());
        Assert.Empty(await NotificationsAsync(P1));
    }

    [SkippableFact]
    // Between a set's games (no config kept: the game ended), or after a game's result: the set's next check-in
    // concedes it for the player who left.
    public async Task AGameClosedBetweenASetsGamesOrAfterAResultFlagsTheSet()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        await SeedSetAsync();
        await Events().GameClosedAsync(P2);
        Assert.Equal(Set, (string?)await Db.StringGetAsync($"ranked_disconnect:{P2}"));

        await SeedAsync();
        await SentConfigAsync(P1);
        await Db.StringSetAsync($"game_result_received:{Match}", "1");
        await Events().GameClosedAsync(P1);
        Assert.Equal(Set, (string?)await Db.StringGetAsync($"ranked_disconnect:{P1}"));
        Assert.False(await Db.KeyExistsAsync($"elo_processed_set:{Set}"));
        Assert.Equal(0, await RatingsAsync());
    }

    [SkippableFact]
    // TS took a spectator's team for the dodger's: a spectator closing their game before the start rated the set.
    public async Task ASpectatorsGameClosingChangesNothing()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        await SeedAsync(WithSpectator());
        await SentConfigAsync(Spectator);

        await Events().GameClosedAsync(Spectator);

        Assert.False(await Db.KeyExistsAsync($"elo_processed:{Match}"));
        Assert.False(await Db.KeyExistsAsync($"ranked_disconnect:{Spectator}"));
        Assert.Equal(0, await RatingsAsync());
        Assert.Empty(await NotificationsAsync(P1));
        Assert.Empty(await NotificationsAsync(P2));
    }

    [SkippableFact]
    public async Task APlayerWhoseWebsocketIsUpWasLostByTheRollbackServerNotADodge()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        await SeedAsync();
        await SeedSetAsync();
        await Db.SetAddAsync("online_players", [P1, P2]);
        await Db.StringSetAsync($"match_started:{Match}", "1");

        await SendAsync(Event("PlayerDisconnect", P2));
        await SendAsync(Event("PlayerDisconnect", P1));

        Assert.True(await Db.KeyExistsAsync($"match_server_crash:{Match}"));
        await AssertSetDroppedAsync();
        Assert.False(await Db.KeyExistsAsync($"match_to_set:{Match}"));
        Assert.False(await Db.KeyExistsAsync($"match_started:{Match}"));
        foreach (string id in new[] { P1, P2 })
        {
            Assert.Equal("idle", (string?)await Db.HashGetAsync($"player:{id}", "status"));
            // Once per match: the second player's disconnect finds the cleanup claimed.
            var cancel = Assert.Single(await NotificationsAsync(id));
            Assert.Equal("match_cancel", cancel["type"]!.GetValue<string>());
            Assert.Equal($$"""{"matchId":"{{Match}}","reason":"rollback_crash"}""", cancel["data"]!.ToJsonString());
        }

        Assert.Equal(0, await RatingsAsync());
    }

    [SkippableFact]
    public async Task ARollbackCrashIsCleanedUpOnceEvenByTwoAtTheSameMoment()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        await SeedAsync();
        await SeedSetAsync();
        await Db.SetAddAsync("online_players", [P1, P2]);
        // Another replica is handling the other player's disconnect: it holds the claim and has not set the crash yet.
        await Db.StringSetAsync($"rollback_crash_cleanup:{Match}", "1");

        await SendAsync(Event("PlayerDisconnect", P2));

        Assert.False(await Db.KeyExistsAsync($"match_server_crash:{Match}"));
        Assert.True(await Db.KeyExistsAsync($"ranked_set:{Set}"));
        Assert.Empty(await NotificationsAsync(P1));
    }

    [SkippableFact]
    public async Task ASpectatorsDisconnectChangesNothing()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        await SeedAsync(WithSpectator());
        await SeedSetAsync();
        await Db.SetAddAsync("online_players", [P1, P2, Spectator]);
        await Db.StringSetAsync($"match_started:{Match}", "1");

        // Websocket up (TS: a rollback crash, the set dropped, everyone sent match_cancel), then down mid-game (TS: a
        // ranked_disconnect for the spectator) and before the start.
        await SendAsync(Event("PlayerDisconnect", Spectator));
        await Db.SetRemoveAsync("online_players", Spectator);
        await SendAsync(Event("PlayerDisconnect", Spectator));
        await Db.KeyDeleteAsync($"match_started:{Match}");
        await SendAsync(Event("PlayerDisconnect", Spectator));

        Assert.False(await Db.KeyExistsAsync($"match_server_crash:{Match}"));
        Assert.False(await Db.KeyExistsAsync($"rollback_crash_cleanup:{Match}"));
        Assert.False(await Db.KeyExistsAsync($"ranked_disconnect:{Spectator}"));
        Assert.True(await Db.KeyExistsAsync($"ranked_set:{Set}"));
        Assert.True(await Db.KeyExistsAsync($"player_ranked_set:{P1}"));
        foreach (string id in new[] { P1, P2, Spectator })
        {
            Assert.Empty(await NotificationsAsync(id));
        }
    }

    [SkippableFact]
    public async Task AMidGameLeaverIsFlaggedForTheSetsNextCheckIn()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        await SeedAsync();
        await Db.SetAddAsync("online_players", P1);
        await Db.StringSetAsync($"match_started:{Match}", "1");

        await SendAsync(Event("PlayerDisconnect", P2));

        // The flag names the set (game 1: the match itself, no set pointer yet), so it counts for this set only.
        Assert.Equal(Match, (string?)await Db.StringGetAsync($"ranked_disconnect:{P2}"));
        Assert.Empty(await NotificationsAsync(P1));
        Assert.Equal(0, await RatingsAsync());
    }

    [SkippableFact]
    public async Task AMidGameLeaverOfAGameThatIsNotASetGameIsNotFlagged()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        // A password match (a rift): RatedMatches does not count it, so there is no set to concede.
        await SeedAsync(match: new JsonObject { ["isPasswordMatch"] = true });
        await Db.SetAddAsync("online_players", P1);
        await Db.StringSetAsync($"match_started:{Match}", "1");

        await SendAsync(Event("PlayerDisconnect", P2));

        Assert.False(await Db.KeyExistsAsync($"ranked_disconnect:{P2}"));
    }

    [SkippableFact]
    public async Task APregameDodgeGivesTheSetToTheOtherTeamOnce()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        await SeedAsync();
        await SeedSetAsync();
        await Db.SetAddAsync("online_players", P1);

        await SendAsync(Event("PlayerDisconnect", P2));
        await SendAsync(Event("PlayerDisconnect", P2));

        var ratings = Mongo.GetCollection<BsonDocument>("eloratings");
        Assert.Equal(1, (await ratings.Find(new BsonDocument("account_id", P1)).FirstAsync())["wins_1v1"].ToInt32());
        Assert.Equal(1, (await ratings.Find(new BsonDocument("account_id", P2)).FirstAsync())["losses_1v1"].ToInt32());
        // Each player's ranks, the dodger's included (connected or not: a result is a result).
        var updates = _published.Select(p => p.Message).ToList();
        Assert.Equal([P1, P2], updates.Select(u => u["message"]!["payload"]!["account_id"]!.GetValue<string>()));
        Assert.All(updates, u => Assert.NotNull(u["message"]!["data"]!["SeasonalData"]!["Season:SeasonSix"]));
        Assert.Equal("rollback_pregame_dodge", (string?)await Db.StringGetAsync($"elo_processed_set:{Set}"));
        Assert.Equal(Set, (string?)await Db.StringGetAsync($"ranked_disconnect:{P2}"));
        await AssertSetDroppedAsync();
        Assert.Equal("idle", (string?)await Db.HashGetAsync($"player:{P2}", "status"));
        var cancel = Assert.Single(await NotificationsAsync(P1));
        Assert.Equal("Opponent left the match", cancel["message"]!.GetValue<string>());
        Assert.Empty(await NotificationsAsync(P2));
    }

    [SkippableFact]
    public async Task AnUnratedDodgeSkipsOnlyTheRating()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        // A password match (a rift): not rated (RatedMatches), but the rest is done as for a rated one (decided 2026-10-05),
        // except the dodger's flag: it names a rated set only.
        await SeedAsync(match: new JsonObject { ["isPasswordMatch"] = true });
        await Db.SetAddAsync("online_players", P1);

        await SendAsync(Event("PlayerDisconnect", P2));

        Assert.Equal(0, await RatingsAsync());
        Assert.Equal(0, await Mongo.GetCollection<BsonDocument>("playerstats").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        await Task.Delay(200);
        Assert.Empty(_published);
        Assert.False(await Db.KeyExistsAsync($"ranked_disconnect:{P2}"));
        Assert.Equal("idle", (string?)await Db.HashGetAsync($"player:{P1}", "status"));
        Assert.Single(await NotificationsAsync(P1));
    }

    [SkippableFact]
    public async Task ADodgeInACustomGameIsLeftAlone()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        await SeedAsync(config: new JsonObject { ["isCustomGame"] = true });
        await Db.SetAddAsync("online_players", P1);

        await SendAsync(Event("PlayerDisconnect", P2));

        Assert.Equal(0, await RatingsAsync());
        Assert.False(await Db.KeyExistsAsync($"ranked_disconnect:{P2}"));
        Assert.Empty(await NotificationsAsync(P1));
    }
}

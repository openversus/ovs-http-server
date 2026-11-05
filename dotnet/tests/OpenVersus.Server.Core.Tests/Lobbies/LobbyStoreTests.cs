using System.Text.Json.Nodes;
using MongoDB.Bson;
using OpenVersus.Server.Core.Lobbies;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Tests.Lobbies;

/// <summary>
/// The lobby records (Lobby, LobbyStore): the kinds a record reads as, the fields kept as read, and a change that loses to
/// another writer running again. Needs Redis (OVS_TEST_REDIS, OVS_TEST_REDIS_USER, OVS_TEST_REDIS_PW), database 15.
/// </summary>
[Collection(RedisTestDatabase.Name)]
public sealed class LobbyStoreTests : IAsyncLifetime
{
    private const int TestRedisDb = 15;
    private const string A = "0000000000000000000b0001", B = "0000000000000000000b0002", C = "0000000000000000000b0003";
    private static readonly string? s_redis = Environment.GetEnvironmentVariable("OVS_TEST_REDIS");
    private ConnectionMultiplexer? _redis;
    private readonly List<string> _keys = [];

    private static bool Configured => !string.IsNullOrEmpty(s_redis);

    private IDatabase Redis => _redis!.GetDatabase(TestRedisDb);

    public async Task InitializeAsync()
    {
        if (!Configured)
        {
            return;
        }

        string[] parts = s_redis!.Split(':');
        _redis = await ConnectionMultiplexer.ConnectAsync(new ConfigurationOptions
        {
            EndPoints = { { parts[0], parts.Length > 1 ? int.Parse(parts[1]) : 6379 } },
            User = Environment.GetEnvironmentVariable("OVS_TEST_REDIS_USER") ?? "",
            Password = Environment.GetEnvironmentVariable("OVS_TEST_REDIS_PW") ?? "",
            DefaultDatabase = TestRedisDb,
        });
    }

    public async Task DisposeAsync()
    {
        if (_redis is null)
        {
            return;
        }

        foreach (string key in _keys)
        {
            await Redis.KeyDeleteAsync(key);
        }

        await _redis.DisposeAsync();
    }

    private async Task<string> StoreAsync(string json, TimeSpan ttl)
    {
        string id = ObjectId.GenerateNewId().ToString();
        await Redis.StringSetAsync(LobbyStore.Key(id), json.Replace("{id}", id), ttl);
        _keys.Add(LobbyStore.Key(id));
        return id;
    }

    [Fact]
    public void ARecordReadsAsTheKindItsModeNames()
    {
        Assert.IsType<PartyLobby>(Lobby.FromJson("x", """{"lobbyId":"x","ownerId":"o","mode":"2v2","playerIds":["o"]}"""));
        var rift = Assert.IsType<RiftLobby>(Lobby.FromJson("x",
            """{"lobbyId":"x","ownerId":"o","mode":"rift_lobby","playerIds":["o"],"riftConfigSlug":"rift_s1","chapterDifficulty":2}"""));
        Assert.Equal(("rift_lobby", "rift_s1", 2), (rift.Template, rift.RiftConfigSlug, rift.ChapterDifficulty));
        Assert.IsType<ArenaLobby>(Lobby.FromJson("x", """{"lobbyId":"x","ownerId":"o","mode":"arena_lobby","playerIds":["o"]}"""));
        // No playerIds (an older custom lobby), or not JSON: no lobby.
        Assert.Null(Lobby.FromJson("x", """{"lobbyId":"x","ownerId":"o"}"""));
        Assert.Null(Lobby.FromJson("x", "not json"));
    }

    [Fact]
    public void FieldsTheBaseDoesNotModelAreWrittenBackWhereTheyWere()
    {
        var lobby = Lobby.FromJson("x", """{"lobbyId":"x","ownerId":"o","ownerUsername":"O","mode":"rift_lobby","playerIds":["o"],"createdAt":1791649860375,"riftConfigSlug":"r","chapterGuid":"g"}""")!;
        lobby.PlayerIds.Add("p");
        lobby.SetField("joinable", false);

        Assert.Equal("""{"lobbyId":"x","ownerId":"o","ownerUsername":"O","mode":"rift_lobby","playerIds":["o","p"],"createdAt":1791649860375,"riftConfigSlug":"r","chapterGuid":"g","joinable":false}""",
            lobby.ToJson());
        Assert.Equal(1791649860, lobby.CreatedSeconds);
    }

    // A duo plays 1v1 as 2v2, ranked 1v1 as ranked 2v2 (both spellings), and FFA (which a duo may not play) as 2v2.
    [Theory]
    [InlineData("1v1", "2v2")]
    [InlineData("", "2v2")]
    [InlineData("ranked-1v1", "ranked-2v2")]
    [InlineData("1v1_ranked", "2v2_ranked")]
    [InlineData("evtq_ffa", "2v2")]
    [InlineData("2v2", "2v2")]
    [InlineData("ranked-2v2", "ranked-2v2")]
    [InlineData("evtq_arena", "evtq_arena")]
    public void ADuoPlaysTheDuoFormOfItsMode(string mode, string duo) => Assert.Equal(duo, PartyLobby.DuoMode(mode));

    [Fact]
    public void ANewPartyLobbyHasTheTsServersFieldsInItsOrder()
    {
        var lobby = new PartyLobby("x", "o", "O", "1v1", ["o"], 1791649860375);
        Assert.Equal("""{"lobbyId":"x","ownerId":"o","ownerUsername":"O","mode":"1v1","playerIds":["o"],"createdAt":1791649860375}""", lobby.ToJson());
    }

    [SkippableFact]
    public async Task AChangeThatLosesToAnotherWriterRunsAgainAndBothLand()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS to run");
        string id = await StoreAsync($$"""{"lobbyId":"{id}","ownerId":"{{A}}","mode":"1v1","playerIds":["{{A}}"]}""", TimeSpan.FromHours(1));

        int runs = 0;
        var changed = await LobbyStore.UpdateAsync(Redis, id, lobby =>
        {
            if (runs++ == 0)
            {
                // Another writer gets in between this change's read and its write.
                LobbyStore.UpdateAsync(Redis, id, other =>
                {
                    other.PlayerIds.Add(B);
                    return LobbyWrite.Save;
                }).GetAwaiter().GetResult();
            }

            lobby.PlayerIds.Add(C);
            return LobbyWrite.Save;
        });

        Assert.Equal(2, runs);
        Assert.Equal(new[] { A, B, C }, changed!.PlayerIds);
        Assert.Equal(new[] { A, B, C }, (await LobbyStore.GetAsync(Redis, id))!.PlayerIds);
        // Three players: the party's 8 hours.
        Assert.InRange((await Redis.KeyTimeToLiveAsync(LobbyStore.Key(id)))!.Value, TimeSpan.FromHours(7.9), LobbyStore.PartyTtl);
    }

    [SkippableFact]
    public async Task AChangeThatAlwaysLosesGivesUpWithAConflict()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS to run");
        string id = await StoreAsync($$"""{"lobbyId":"{id}","ownerId":"{{A}}","mode":"1v1","playerIds":["{{A}}"]}""", TimeSpan.FromHours(1));

        int runs = 0;
        await Assert.ThrowsAsync<LobbyConflictException>(() => LobbyStore.UpdateAsync(Redis, id, lobby =>
        {
            runs++;
            // Another writer changes the record every time (a field of its own, new each round).
            string stored = Redis.StringGet(LobbyStore.Key(id)).ToString();
            Redis.StringSet(LobbyStore.Key(id), $"{stored[..^1]},\"other{runs}\":{runs}}}");
            return LobbyWrite.Save;
        }));
        Assert.Equal(LobbyStore.Attempts, runs);
    }

    [SkippableFact]
    public async Task KeepWritesNothingSaveForAndDeleteDoWhatTheySay()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS to run");
        string id = await StoreAsync($$"""{"lobbyId":"{id}","ownerId":"{{A}}","mode":"1v1","playerIds":["{{A}}","{{B}}"]}""", TimeSpan.FromMinutes(5));

        Assert.NotNull(await LobbyStore.UpdateAsync(Redis, id, _ => LobbyWrite.Keep));
        Assert.InRange((await Redis.KeyTimeToLiveAsync(LobbyStore.Key(id)))!.Value, TimeSpan.Zero, TimeSpan.FromMinutes(5));

        await LobbyStore.UpdateAsync(Redis, id, _ => LobbyWrite.SaveFor(LobbyStore.SoloTtl));
        Assert.InRange((await Redis.KeyTimeToLiveAsync(LobbyStore.Key(id)))!.Value, TimeSpan.FromMinutes(59), LobbyStore.SoloTtl);

        await LobbyStore.UpdateAsync(Redis, id, _ => LobbyWrite.Delete);
        Assert.False(await Redis.KeyExistsAsync(LobbyStore.Key(id)));
        Assert.Null(await LobbyStore.UpdateAsync(Redis, id, _ => throw new InvalidOperationException("no lobby: the change never runs")));
    }
}

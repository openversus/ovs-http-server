using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using OpenVersus.Server.Core.Arenas;
using OpenVersus.Server.Core.Matches;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Tests.Arenas;

/// <summary>
/// lobby_code from an Arena lobby (the lobby screen's eye button): one code per lobby, for any member. Needs Redis
/// (OVS_TEST_REDIS, OVS_TEST_REDIS_USER, OVS_TEST_REDIS_PW), database 15.
/// </summary>
[Collection(RedisTestDatabase.Name)]
public sealed class ArenaLobbyCodeTests : IAsyncLifetime
{
    private const int TestRedisDb = 15;
    private const string Me = "0000000000000000000a0001", Other = "0000000000000000000a0002";
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

    private ArenaLobbyService Service() => new(new ServiceCollection().AddSingleton<IConnectionMultiplexer>(_redis!).BuildServiceProvider(),
        new StaticMonitor(new LobbySettings()), TimeProvider.System, NullLogger<ArenaLobbyService>.Instance);

    private async Task<string> LobbyAsync(string mode = ArenaLobbyService.Mode)
    {
        string id = ObjectId.GenerateNewId().ToString();
        await Redis.StringSetAsync($"lobby:{id}", $$"""{"lobbyId":"{{id}}","mode":"{{mode}}","playerIds":["{{Me}}"]}""", TimeSpan.FromHours(1));
        _keys.AddRange([$"lobby:{id}", ArenaLobbyService.CodeKey(id)]);
        return id;
    }

    private async Task<JsonObject?> AskAsync(string player, string lobbyId) =>
        await Service().LobbyCodeAsync(player, new JsonObject { ["LobbyId"] = lobbyId }, CancellationToken.None);

    [SkippableFact]
    public async Task TheFirstAskDrawsTheCodeAndEveryLaterOneGetsItAgain()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS to run");
        string lobby = await LobbyAsync();

        string code = (string)(await AskAsync(Me, lobby))!["body"]!["LobbyCode"]!;
        _keys.Add(LobbyCodes.Key(code));

        Assert.Matches("^[" + LobbyCodes.Alphabet + "]{5}$", code);
        Assert.Equal(lobby, (string?)await Redis.StringGetAsync(LobbyCodes.Key(code)));
        Assert.Equal(code, (string)(await AskAsync(Me, lobby))!["body"]!["LobbyCode"]!);
        // As long as the lobby has left.
        Assert.InRange((await Redis.KeyTimeToLiveAsync(LobbyCodes.Key(code)))!.Value, TimeSpan.FromMinutes(59), TimeSpan.FromHours(1));
    }

    [SkippableFact]
    public async Task SomeoneNotInTheLobbyGetsNoCode()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS to run");
        string lobby = await LobbyAsync();

        var answer = await AskAsync(Other, lobby);

        Assert.Null(answer!["body"]!["LobbyCode"]);
        Assert.False(await Redis.KeyExistsAsync(ArenaLobbyService.CodeKey(lobby)));
    }

    [SkippableFact]
    public async Task AnotherKindOfLobbyIsLeftToItsOwnService()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS to run");
        string party = await LobbyAsync("party_lobby");

        Assert.Null(await AskAsync(Me, party));
        Assert.Null(await AskAsync(Me, ObjectId.GenerateNewId().ToString()));
    }

    private sealed class StaticMonitor(LobbySettings value) : IOptionsMonitor<LobbySettings>
    {
        public LobbySettings CurrentValue => value;

        public LobbySettings Get(string? name) => value;

        public IDisposable? OnChange(Action<LobbySettings, string?> listener) => null;
    }
}

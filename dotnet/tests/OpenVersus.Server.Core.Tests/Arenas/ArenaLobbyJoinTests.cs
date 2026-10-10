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
/// join_arena_lobby (an accepted Arena invite) and the invite a party's partner gets when the other enters an Arena. Needs
/// Redis (OVS_TEST_REDIS, OVS_TEST_REDIS_USER, OVS_TEST_REDIS_PW), database 15.
/// </summary>
[Collection(RedisTestDatabase.Name)]
public sealed class ArenaLobbyJoinTests : IAsyncLifetime
{
    private const int TestRedisDb = 15;
    private const string Me = "0000000000000000000a0001", Other = "0000000000000000000a0002", Third = "0000000000000000000a0003";
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

    private async Task<string> LobbyAsync(string mode, params string[] players)
    {
        string id = ObjectId.GenerateNewId().ToString();
        string ids = string.Join(",", players.Select(p => $"\"{p}\""));
        await Redis.StringSetAsync($"lobby:{id}", $$"""{"lobbyId":"{{id}}","ownerId":"{{players[0]}}","mode":"{{mode}}","playerIds":[{{ids}}],"createdAt":1790000000123}""", TimeSpan.FromHours(1));
        _keys.Add($"lobby:{id}");
        return id;
    }

    private async Task<JsonObject> JoinAsync(string player, string? invitedTo)
    {
        _keys.AddRange([$"player_lobby:{player}", $"pending_join_lobby:{player}", $"connections:{player}"]);
        if (invitedTo is not null)
        {
            await Redis.StringSetAsync($"pending_join_lobby:{player}", invitedTo, TimeSpan.FromSeconds(60));
        }

        return await Service().JoinAsync(new JsonObject { ["id"] = player }, new JsonObject { ["LobbyId"] = "the player's own lobby" }, CancellationToken.None);
    }

    private static JsonObject Players(JsonObject answer, int team) => answer["body"]!["lobby"]!["Teams"]![team]!["Players"]!.AsObject();

    [SkippableFact]
    public async Task AnInvitedPlayerJoinsOnTheLeadersTeamAndIsAnsweredTheLobby()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS to run");
        string lobby = await LobbyAsync(ArenaLobbyService.Mode, Me);

        var answer = await JoinAsync(Other, lobby);

        Assert.Equal(0, (int)answer["return_code"]!);
        Assert.Equal(8, answer["body"]!["lobby"]!["Teams"]!.AsArray().Count);
        Assert.Equal([Me, Other], Players(answer, 0).Select(p => p.Key));
        Assert.Equal(Me, (string?)answer["body"]!["lobby"]!["LeaderID"]);
        Assert.Equal(lobby, (string?)answer["body"]!["lobby"]!["MatchID"]);
        var stored = JsonNode.Parse((string)(await Redis.StringGetAsync($"lobby:{lobby}"))!)!;
        Assert.Equal(0, (int)stored["teams"]![Other]!);
        Assert.Equal(lobby, (string?)await Redis.StringGetAsync($"player_lobby:{Other}"));
        Assert.False(await Redis.KeyExistsAsync($"pending_join_lobby:{Other}"));
    }

    [SkippableFact]
    public async Task ATeamOfTwoIsFullSoTheNextPlayerGoesToTheNextTeam()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS to run");
        string lobby = await LobbyAsync(ArenaLobbyService.Mode, Me);
        await JoinAsync(Other, lobby);

        var answer = await JoinAsync(Third, lobby);

        Assert.Equal([Me, Other], Players(answer, 0).Select(p => p.Key));
        Assert.Equal([Third], Players(answer, 1).Select(p => p.Key));
    }

    [SkippableFact]
    public async Task AFullLobbyAndAnotherKindOfLobbyAreRefused()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS to run");
        string full = await LobbyAsync(ArenaLobbyService.Mode, [.. Enumerable.Range(0, 16).Select(i => $"00000000000000000000{i:x4}")]);
        string party = await LobbyAsync("2v2", Me);

        Assert.Equal(1, (int)(await JoinAsync(Other, full))["return_code"]!);
        Assert.Equal(1, (int)(await JoinAsync(Other, party))["return_code"]!);
        Assert.DoesNotContain(Other, (string)(await Redis.StringGetAsync($"lobby:{full}"))!);
    }

    [SkippableFact]
    public async Task EnteringAnArenaFromAPartyInvitesThePartner()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS to run");
        string party = await LobbyAsync("2v2", Me, Other);
        await Redis.StringSetAsync($"player_lobby:{Me}", party, TimeSpan.FromHours(1));
        _keys.AddRange([$"player_lobby:{Me}", $"pending_join_lobby:{Other}", $"connections:{Me}"]);

        var created = await Service().CreateAsync(new JsonObject { ["id"] = Me }, new JsonObject { ["LobbyTemplate"] = "arena_lobby" }, CancellationToken.None);

        string arena = (string)created["body"]!["lobby"]!["MatchID"]!;
        _keys.Add($"lobby:{arena}");
        Assert.Equal(arena, (string?)await Redis.StringGetAsync($"pending_join_lobby:{Other}"));
        // The party lobby stays, with both in it: where they go back to.
        Assert.Contains(Other, (string)(await Redis.StringGetAsync($"lobby:{party}"))!);
    }

    // A party of Me and Other in party lobby P, Me presses Arena, Other accepts: the Arena lobby, both with home P.
    private async Task<(string Party, string Arena)> PartyInArenaAsync()
    {
        string party = await LobbyAsync("2v2", Me, Other);
        await Redis.StringSetAsync($"player_lobby:{Me}", party, TimeSpan.FromHours(1));
        await Redis.StringSetAsync($"player_lobby:{Other}", party, TimeSpan.FromHours(1));
        _keys.AddRange([$"player_lobby:{Me}", $"connections:{Me}"]);
        var created = await Service().CreateAsync(new JsonObject { ["id"] = Me }, new JsonObject { ["LobbyTemplate"] = "arena_lobby" }, CancellationToken.None);
        string arena = (string)created["body"]!["lobby"]!["MatchID"]!;
        _keys.Add($"lobby:{arena}");
        await JoinAsync(Other, invitedTo: null);
        return (party, arena);
    }

    [SkippableFact]
    public async Task JoinTeamMovesThePlayerWhenTheTeamHasRoom()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS to run");
        var (_, arena) = await PartyInArenaAsync();

        var moved = await Service().SwitchTeamAsync(Other, new JsonObject { ["MatchID"] = arena, ["TeamIndex"] = 3 }, CancellationToken.None);
        await Service().SwitchTeamAsync(Third, new JsonObject { ["MatchID"] = arena, ["TeamIndex"] = 3 }, CancellationToken.None);
        await JoinAsync(Third, arena);
        var refused = await Service().SwitchTeamAsync(Me, new JsonObject { ["MatchID"] = arena, ["TeamIndex"] = 9 }, CancellationToken.None);

        Assert.Equal(3, (int)moved!["body"]!["TeamIndex"]!);
        var stored = JsonNode.Parse((string)(await Redis.StringGetAsync($"lobby:{arena}"))!)!;
        Assert.Equal(3, (int)stored["teams"]![Other]!);
        Assert.Empty(refused!);
        Assert.Null(await Service().SwitchTeamAsync(Me, new JsonObject { ["MatchID"] = await LobbyAsync("2v2", Me), ["TeamIndex"] = 1 }, CancellationToken.None));
    }

    [SkippableFact]
    public async Task OnlyTheLeaderKicksAndTheKickDisbandsTheKickedPlayersParty()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS to run");
        var (party, arena) = await PartyInArenaAsync();

        var refused = await Service().KickAsync(Other, new JsonObject { ["KickeeAccountID"] = Me, ["LobbyId"] = arena }, CancellationToken.None);
        var kicked = await Service().KickAsync(Me, new JsonObject { ["KickeeAccountID"] = Other, ["LobbyId"] = arena }, CancellationToken.None);

        Assert.Equal(1, (int)refused!["return_code"]!);
        Assert.Equal(0, (int)kicked!["return_code"]!);
        Assert.DoesNotContain(Other, (string)(await Redis.StringGetAsync($"lobby:{arena}"))!);
        // Their party is over: the party lobby keeps only the leader, and the kicked player's pointer is gone.
        Assert.DoesNotContain(Other, (string)(await Redis.StringGetAsync($"lobby:{party}"))!);
        Assert.False(await Redis.KeyExistsAsync($"player_lobby:{Other}"));
    }

    [SkippableFact]
    public async Task LeavingTakesTheLeaverHomePassesTheLeadAndInvitesThePartnerBack()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS to run");
        var (party, arena) = await PartyInArenaAsync();

        string? home = await Service().LeaveAsync(Me, arena);

        Assert.Equal(party, home);
        Assert.Equal(party, (string?)await Redis.StringGetAsync($"player_lobby:{Me}"));
        var stored = JsonNode.Parse((string)(await Redis.StringGetAsync($"lobby:{arena}"))!)!;
        Assert.Equal(Other, (string?)stored["ownerId"]);
        Assert.Equal([Other], stored["playerIds"]!.AsArray().Select(p => (string)p!));
        Assert.Equal(party, (string?)await Redis.StringGetAsync($"pending_join_lobby:{Other}"));
    }

    private sealed class StaticMonitor(LobbySettings value) : IOptionsMonitor<LobbySettings>
    {
        public LobbySettings CurrentValue => value;

        public LobbySettings Get(string? name) => value;

        public IDisposable? OnChange(Action<LobbySettings, string?> listener) => null;
    }
}

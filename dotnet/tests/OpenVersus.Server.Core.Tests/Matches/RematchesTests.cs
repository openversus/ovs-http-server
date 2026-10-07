using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenVersus.Server.Core.Clients;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.CustomLobbies;
using OpenVersus.Server.Core.Identity;
using OpenVersus.Server.Core.Matches;
using OpenVersus.Server.Core.Realtime;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Tests.Matches;

/// <summary>
/// What only the C# rematch does (Rematches): one rematch however many accepts and timers reach for it, a spectator's
/// accept not counting for a player's, a rematch that cannot start declined, and the Casual rematch, which the TS
/// server never had. Parity with the TS lobby rematch is tools/matches/match_end_diff.mjs. Real Redis, database 15.
/// </summary>
[Collection(RedisTestDatabase.Name)]
public sealed class RematchesTests : IAsyncLifetime
{
    private static readonly string? s_redis = Environment.GetEnvironmentVariable("OVS_TEST_REDIS");
    private static string Id(int n) => $"00000000000000000021{n:D4}";
    private static readonly string Match = Id(100), Lobby = Id(200), P1 = Id(1), P2 = Id(2), Spectator = Id(3);
    private const string Bot = "Bot00000000000000000021000000000001";

    private ConnectionMultiplexer? _redis;
    private readonly List<string> _sent = [];

    private sealed class Lobbies(bool starts = true) : ICustomLobbyService
    {
        public int Rematches { get; private set; }

        public Task<bool> RematchAsync(string lobbyId, CancellationToken ct)
        {
            Rematches++;
            return Task.FromResult(starts);
        }

        public Task<JsonObject> AnswerAsync(string route, PartyRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<JsonObject?> SharedAsync(string route, PartyRequest request, string lobbyId, CancellationToken ct) => throw new NotSupportedException();
        public Task<JsonObject?> ByCodeAsync(string code, CancellationToken ct) => throw new NotSupportedException();
        public Task PlayerDisconnectedAsync(string playerId) => throw new NotSupportedException();
        public Task<bool> LeaveLobbyFromBeforeLoginAsync(string playerId) => throw new NotSupportedException();
    }

    private sealed class Launcher : IMatchLauncher
    {
        public List<MatchLaunch> Launched { get; } = [];

        public Task<LaunchedMatch?> LaunchAsync(MatchLaunch launch, CancellationToken ct)
        {
            Launched.Add(launch);
            return Task.FromResult<LaunchedMatch?>(new LaunchedMatch(Id(900), 57001));
        }

        public Task<int?> RollbackPortAsync(IDatabase redis) => Task.FromResult<int?>(57001);

        public void DeployIfOnDemand(int port, string matchId)
        {
        }
    }

    private sealed class CurrentClients : IClientUpdateGate
    {
        public Task<IReadOnlyList<ClientUpdateState>> RequiringUpdateAsync(IEnumerable<string> playerIds) => Task.FromResult<IReadOnlyList<ClientUpdateState>>([]);
        public Task<IReadOnlyList<bool>> RequestModalsAsync(IEnumerable<string> playerIds) => Task.FromResult<IReadOnlyList<bool>>([]);
        public Task<ClientUpdateState> ForRequestAsync(AccountLookup lookup, JsonObject? claims) => throw new NotSupportedException();
        public Task<double> ModalNonceAsync(string playerId) => throw new NotSupportedException();
        public JsonObject FailureBody() => throw new NotSupportedException();
    }

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
        await _redis.GetSubscriber().SubscribeAsync(RedisChannel.Literal(ProfileNotifications.WsSendChannel), (_, m) =>
        {
            if (m.ToString().Contains("00000000000000000021", StringComparison.Ordinal))
            {
                lock (_sent)
                {
                    _sent.Add(m.ToString());
                }
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
        foreach (var key in _redis!.GetServer(_redis.GetEndPoints()[0]).Keys(15, "*00000000000000000021*"))
        {
            await Db.KeyDeleteAsync(key);
        }

        await Db.KeyDeleteAsync(RematchVotes.DueKey);
    }

    private IDatabase Db => _redis!.GetDatabase();

    private IRematches Service(ICustomLobbyService lobbies, Launcher? launcher = null) => new Rematches(
        new ServiceCollection().AddSingleton<IConnectionMultiplexer>(_redis!).BuildServiceProvider(),
        lobbies, launcher ?? new Launcher(), new CurrentClients(), NullLogger<Rematches>.Instance);

    // P1 in team 0; P2 and a bot in team 1; a spectator in team 4. P1 was ready.
    private async Task OpenLobbyVoteAsync()
    {
        static JsonObject Team(int index, params (string Id, string Bot)[] players) => new()
        {
            ["TeamIndex"] = index,
            ["Players"] = new JsonObject(players.Select(p => KeyValuePair.Create(p.Id, (JsonNode?)new JsonObject { ["BotSettingSlug"] = p.Bot }))),
        };

        await Db.StringSetAsync($"custom_lobby_ssc:{Lobby}", Js.Stringify(new JsonObject
        {
            ["Teams"] = new JsonArray(Team(0, (P1, "")), Team(1, (P2, ""), (Bot, "Medium")), Team(4, (Spectator, ""))),
            ["LeaderID"] = P1,
            ["ReadyPlayers"] = new JsonObject { [P1] = true },
        }));
        foreach (string id in new[] { P1, P2, Spectator })
        {
            await Db.StringSetAsync(RematchVotes.LobbyPlayerKey(id), Lobby);
        }

        await Db.StringSetAsync($"ssc_custom_lobby_match:{Match}", Lobby);
        Assert.True(await RematchVotes.OpenLobbyAsync(Db, TimeProvider.System, Match, Lobby));
    }

    private static JsonObject CasualConfig() => new()
    {
        ["players"] = new JsonArray(
            new JsonObject { ["playerId"] = P1, ["partyId"] = "party-a", ["playerIndex"] = 0, ["teamIndex"] = 0, ["isHost"] = true, ["ip"] = "198.51.100.1", ["isBot"] = false },
            new JsonObject { ["playerId"] = P2, ["partyId"] = "party-b", ["playerIndex"] = 2, ["teamIndex"] = 0, ["isHost"] = false, ["ip"] = "198.51.100.2", ["isBot"] = false },
            new JsonObject { ["playerId"] = Bot, ["partyId"] = Match, ["playerIndex"] = 1, ["teamIndex"] = 1, ["isHost"] = false, ["ip"] = "", ["isBot"] = true }),
        ["matchId"] = Match,
        ["map"] = "M001",
        ["mode"] = "2v2",
        ["isCustomGame"] = true,
        ["gameplayConfigOverride"] = new JsonObject { ["bIsCustomGame"] = false },
    };

    private async Task<List<string>> SentAsync(int atLeast, int waitMs = 1500)
    {
        for (int waited = 0; waited < waitMs; waited += 50)
        {
            lock (_sent)
            {
                if (_sent.Count >= atLeast)
                {
                    break;
                }
            }

            await Task.Delay(50);
        }

        await Task.Delay(200);
        lock (_sent)
        {
            return [.. _sent];
        }
    }

    [SkippableFact]
    public async Task EveryAcceptAndTheTimerStartOneRematch()
    {
        Skip.If(string.IsNullOrEmpty(s_redis), "set OVS_TEST_REDIS to run");
        await OpenLobbyVoteAsync();
        Assert.Equal("{}", Js.Parse((await Db.StringGetAsync($"custom_lobby_ssc:{Lobby}")).ToString())!["ReadyPlayers"]!.ToJsonString());
        var lobbies = new Lobbies();
        var rematches = Service(lobbies);

        // Both players' accepts landed before either counted them: the first to count starts the rematch.
        await Db.SetAddAsync(RematchVotes.LobbyAcceptKey(Lobby), [P1, P2]);
        await rematches.AcceptAsync(P1);
        // The timer, on another replica, checks nothing before its claim: only the claim keeps it from a second rematch
        // (the TS timer's GET came first, and an accept between that and its DEL started one each).
        await rematches.TimerAsync($"lobby:{Lobby}:{Match}");
        await rematches.AcceptAsync(P2);

        Assert.Equal(1, lobbies.Rematches);
        Assert.False(await Db.KeyExistsAsync(RematchVotes.LobbyTimerKey(Lobby)));
    }

    [SkippableFact]
    public async Task ASpectatorsAcceptDoesNotCountForAPlayers()
    {
        Skip.If(string.IsNullOrEmpty(s_redis), "set OVS_TEST_REDIS to run");
        await OpenLobbyVoteAsync();
        var lobbies = new Lobbies();
        var rematches = Service(lobbies);

        // Two accepts for two players: the TS server started the rematch here.
        await rematches.AcceptAsync(P1);
        await rematches.AcceptAsync(Spectator);
        Assert.Equal(0, lobbies.Rematches);

        await rematches.AcceptAsync(P2);
        Assert.Equal(1, lobbies.Rematches);
    }

    [SkippableFact]
    public async Task ARematchThatCannotStartIsDeclinedForEveryone()
    {
        Skip.If(string.IsNullOrEmpty(s_redis), "set OVS_TEST_REDIS to run");
        await OpenLobbyVoteAsync();
        var lobbies = new Lobbies(starts: false);

        await Service(lobbies).TimerAsync($"lobby:{Lobby}:{Match}");

        Assert.Equal(1, lobbies.Rematches);
        var sent = await SentAsync(3);
        Assert.Equal(3, sent.Count(m => m.Contains("RematchDeclinedNotification", StringComparison.Ordinal)));
        Assert.Contains(sent, m => m.Contains(Spectator, StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task ATimerFromAnEarlierVoteLeavesTheLobbysNewVoteAlone()
    {
        Skip.If(string.IsNullOrEmpty(s_redis), "set OVS_TEST_REDIS to run");
        await OpenLobbyVoteAsync();
        var lobbies = new Lobbies();

        await Service(lobbies).TimerAsync($"lobby:{Lobby}:{Id(99)}");

        Assert.Equal(0, lobbies.Rematches);
        Assert.Equal(Match, (string?)await Db.StringGetAsync(RematchVotes.LobbyTimerKey(Lobby)));
    }

    [SkippableFact]
    public async Task ACasualMatchIsPlayedAgainByTheSamePlayers()
    {
        Skip.If(string.IsNullOrEmpty(s_redis), "set OVS_TEST_REDIS to run");
        await RematchVotes.OpenCasualAsync(Db, TimeProvider.System, Match, CasualConfig());
        var launcher = new Launcher();
        var rematches = Service(new Lobbies(), launcher);

        await rematches.AcceptAsync(P1);
        Assert.Empty(launcher.Launched);
        await rematches.AcceptAsync(P2);
        // The timer, after: the vote was over.
        await rematches.TimerAsync($"casual:{Match}");

        var launch = Assert.Single(launcher.Launched);
        Assert.Equal("2v2", launch.Mode);
        Assert.Equal(
            [new MatchPlayer(P1, 0, 0, true, "198.51.100.1", false, false, "party-a"), new MatchPlayer(P2, 2, 0, false, "198.51.100.2", false, false, "party-b"),
             new MatchPlayer(Bot, 1, 1, false, "", true, false, Match)],
            launch.Players);
        Assert.True(launch.NotificationFields!["isCustomGame"]!.GetValue<bool>());
        Assert.False(launch.GameplayConfigOverride!["bIsCustomGame"]!.GetValue<bool>());
        Assert.Equal(BotDefaults.PerksArray().ToJsonString(), launch.BotPerks!.ToJsonString());
        foreach (string key in new[] { RematchVotes.CasualKey(Match), RematchVotes.CasualAcceptKey(Match), RematchVotes.CasualPlayerKey(P1), RematchVotes.CasualPlayerKey(P2) })
        {
            Assert.False(await Db.KeyExistsAsync(key), key);
        }
    }

    [SkippableFact]
    public async Task ACasualDeclineSendsEveryoneBackAndEndsTheVote()
    {
        Skip.If(string.IsNullOrEmpty(s_redis), "set OVS_TEST_REDIS to run");
        await RematchVotes.OpenCasualAsync(Db, TimeProvider.System, Match, CasualConfig());
        var launcher = new Launcher();
        var rematches = Service(new Lobbies(), launcher);

        await rematches.AcceptAsync(P1);
        await rematches.DeclineAsync(P2);
        await rematches.TimerAsync($"casual:{Match}");

        Assert.Empty(launcher.Launched);
        var sent = await SentAsync(2);
        Assert.Equal(2, sent.Count);
        Assert.All(sent, m => Assert.Contains($"\"MatchId\":\"{Match}\"", m, StringComparison.Ordinal));
        Assert.All(sent, m => Assert.Contains("RematchDeclinedNotification", m, StringComparison.Ordinal));
    }
}

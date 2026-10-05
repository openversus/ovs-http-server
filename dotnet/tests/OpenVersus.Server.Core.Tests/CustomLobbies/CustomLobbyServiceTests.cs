using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenVersus.Server.Core.Clients;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.CustomLobbies;
using OpenVersus.Server.Core.Identity;
using OpenVersus.Server.Core.Matches;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Tests.CustomLobbies;

/// <summary>
/// The custom lobby (CustomLobbyService) on real Redis, database 15 (OVS_TEST_REDIS[_USER/_PW]): the Lua scripts run
/// there. Parity with the TS server is tools/matches/custom_lobby_diff.mjs; these hold the TS bugs the port fixes, and
/// what the match start hands the launcher.
/// </summary>
[Collection(RedisTestDatabase.Name)]
public sealed class CustomLobbyServiceTests : IAsyncLifetime
{
    private static readonly string? s_redis = Environment.GetEnvironmentVariable("OVS_TEST_REDIS");
    private ConnectionMultiplexer? _redis;

    // Ids no real player has; every key a test makes holds one of them.
    private const string Leader = "0000000000000000000e0001", Guest = "0000000000000000000e0002", Third = "0000000000000000000e0003", Lobby = "0000000000000000000e0101";
    private const string Bot = "Bot0000000000000000000e00000000001";

    private sealed class Launcher : IMatchLauncher
    {
        public MatchLaunch? Launched { get; private set; }

        public Task<LaunchedMatch?> LaunchAsync(MatchLaunch launch, CancellationToken ct)
        {
            Launched = launch;
            return Task.FromResult<LaunchedMatch?>(new LaunchedMatch("0000000000000000000e0900", 57001));
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
        foreach (var key in server.Keys(15, "*0000000000000000000e*").Concat(server.Keys(15, "*e00000000001")))
        {
            await _redis.GetDatabase().KeyDeleteAsync(key);
        }
    }

    private IDatabase Db => _redis!.GetDatabase();

    private ICustomLobbyService Service(Launcher? launcher = null) => new CustomLobbyService(
        new ServiceCollection().AddSingleton<IConnectionMultiplexer>(_redis!).BuildServiceProvider(),
        launcher ?? new Launcher(), new CurrentClients(), new TestOptions<LobbySettings>(new LobbySettings()),
        new TestOptions<CustomLobbySettings>(new CustomLobbySettings()), TimeProvider.System, NullLogger<CustomLobbyService>.Instance);

    private static PartyRequest Asking(string player, JsonObject body) => new(player, null, "198.51.100.7", body);

    private async Task<JsonObject> StoredAsync() => (JsonObject)Js.Parse((await Db.StringGetAsync($"custom_lobby_ssc:{Lobby}")).ToString())!;

    private static IEnumerable<(int Team, string Id)> Members(JsonObject lobby) =>
        lobby["Teams"]!.AsArray().SelectMany(t => t!["Players"] is JsonObject players ? players.Select(p => (t["TeamIndex"]!.GetValue<int>(), p.Key)) : []);

    private static int Counted(JsonObject lobby) => lobby["Teams"]!.AsArray().Sum(t => t!["Length"]!.GetValue<int>());

    private static JsonObject Player(string id, string joinedAt, string bot = "") => new()
    {
        ["Account"] = new JsonObject { ["id"] = id },
        ["JoinedAt"] = joinedAt,
        ["BotSettingSlug"] = bot,
        ["LobbyPlayerIndex"] = 0,
        ["CrossplayPreference"] = 1,
    };

    /// <summary>A stored lobby led by <see cref="Leader"/>, with <paramref name="teams"/> (team index: players).</summary>
    private Task SeedAsync(string style, params (int Team, string Id, JsonObject Player)[] teams)
    {
        var lobby = new JsonObject
        {
            ["Teams"] = new JsonArray([.. Enumerable.Range(0, 5).Select(t =>
            {
                var players = new JsonObject();
                foreach (var p in teams.Where(p => p.Team == t))
                {
                    players[p.Id] = p.Player;
                }

                return (JsonNode)new JsonObject { ["TeamIndex"] = t, ["Players"] = players, ["Length"] = players.Count };
            })]),
            ["LeaderID"] = Leader,
            ["ReadyPlayers"] = new JsonObject { [Leader] = true },
            ["PlayerGameplayPreferences"] = new JsonObject(teams.Where(p => p.Player["BotSettingSlug"]!.GetValue<string>() == "").Select(p => KeyValuePair.Create(p.Id, (JsonNode?)448))),
            ["PlayerAutoPartyPreferences"] = new JsonObject(),
            ["Platforms"] = new JsonObject(),
            ["LockedLoadouts"] = new JsonObject(),
            ["IsLobbyJoinable"] = true,
            ["MatchID"] = Lobby,
            ["GameModeSlug"] = style switch { "Duos" => "gm_classic_2v2", "FFA" => "gm_classic_ffa", "Other" => "gm_targetbreak", _ => "gm_classic_1v1" },
            ["match_config"] = new JsonObject { ["TeamStyle"] = style, ["NumRingoutsForWin"] = 4, ["MatchDuration"] = 420, ["AllowHazards"] = true, ["EnableShields"] = 1 },
            ["Maps"] = new JsonArray(new JsonObject { ["Map"] = "M001", ["IsSelected"] = true }),
            ["WorldBuffs"] = new JsonArray(),
            ["PlayerBuffs"] = new JsonObject(),
            ["Handicaps"] = new JsonObject(),
        };
        return Db.StringSetAsync($"custom_lobby_ssc:{Lobby}", Js.Stringify(lobby), TimeSpan.FromMinutes(5));
    }

    [Fact]
    // The TS script added a player who joined again to a second team: the lobby counted them twice and could never be
    // all ready.
    public async Task APlayerWhoJoinsAgainIsInTheLobbyOnceAndItCanBeAllReady()
    {
        if (_redis is null)
        {
            return;
        }

        var service = Service();
        await SeedAsync("Duos", (0, Leader, Player(Leader, "2026-10-01T10:00:00.000Z")));
        var join = new JsonObject { ["HostId"] = Lobby, ["IsSpectator"] = false };

        await service.AnswerAsync("join_custom_game_lobby", Asking(Guest, join));
        var again = await service.AnswerAsync("join_custom_game_lobby", Asking(Guest, join));

        Assert.Equal(0, again["return_code"]!.GetValue<int>());
        var lobby = await StoredAsync();
        Assert.Single(Members(lobby), m => m.Id == Guest);
        Assert.Equal(2, Counted(lobby));
        var ready = await service.SharedAsync("set_ready_for_lobby", Asking(Guest, new JsonObject { ["Ready"] = true }), Lobby);
        Assert.True(ready!["body"]!["bAllPlayersReady"]!.GetValue<bool>());
    }

    [Fact]
    // The TS script gave the lead to the first player of the first team with anyone left, a bot included.
    public async Task TheLeadPassesToThePlayerWhoJoinedFirstNeverABot()
    {
        if (_redis is null)
        {
            return;
        }

        var service = Service();
        await SeedAsync("Duos",
            (0, Leader, Player(Leader, "2026-10-01T10:00:00.000Z")),
            (0, Bot, Player(Bot, "2026-10-01T10:00:01.000Z", "Medium")),
            (0, Third, Player(Third, "2026-10-01T10:00:03.000Z")),
            (4, Guest, Player(Guest, "2026-10-01T10:00:02.000Z")));

        await service.SharedAsync("leave_player_lobby", Asking(Leader, new JsonObject { ["LobbyId"] = Lobby }), Lobby);

        var lobby = await StoredAsync();
        Assert.Equal(Guest, lobby["LeaderID"]!.GetValue<string>());
        Assert.True(lobby["ReadyPlayers"]![Guest]!.GetValue<bool>());

        // With only a bot left, the lobby goes.
        await service.SharedAsync("leave_player_lobby", Asking(Third, new JsonObject { ["LobbyId"] = Lobby }), Lobby);
        await service.SharedAsync("leave_player_lobby", Asking(Guest, new JsonObject { ["LobbyId"] = Lobby }), Lobby);
        Assert.False(await Db.KeyExistsAsync($"custom_lobby_ssc:{Lobby}"));
    }

    [Fact]
    // The TS script let any member promote anyone, themselves included.
    public async Task OnlyTheLeaderPromotesAndNeverABot()
    {
        if (_redis is null)
        {
            return;
        }

        var service = Service();
        await SeedAsync("Duos", (0, Leader, Player(Leader, "2026-10-01T10:00:00.000Z")), (0, Guest, Player(Guest, "2026-10-01T10:00:01.000Z")),
            (1, Bot, Player(Bot, "2026-10-01T10:00:02.000Z", "Hard")));

        var byGuest = await service.AnswerAsync("promote_to_lobby_leader", Asking(Guest, new JsonObject { ["MatchID"] = Lobby, ["PromoteTarget"] = Guest }));
        var toBot = await service.AnswerAsync("promote_to_lobby_leader", Asking(Leader, new JsonObject { ["MatchID"] = Lobby, ["PromoteTarget"] = Bot }));
        Assert.Equal(1, byGuest["return_code"]!.GetValue<int>());
        Assert.Equal(1, toBot["return_code"]!.GetValue<int>());
        Assert.Equal(Leader, (await StoredAsync())["LeaderID"]!.GetValue<string>());

        var byLeader = await service.AnswerAsync("promote_to_lobby_leader", Asking(Leader, new JsonObject { ["MatchID"] = Lobby, ["PromoteTarget"] = Guest }));
        Assert.Equal(0, byLeader["return_code"]!.GetValue<int>());
        Assert.Equal(Guest, (await StoredAsync())["LeaderID"]!.GetValue<string>());
    }

    [Theory]
    // The TS script let a player change teams only in Duos (or through the spectators): the first four were refused there.
    [InlineData("FFA", 4, 1, 3, true)]
    [InlineData("Solos", 4, 1, 0, true)]
    [InlineData("Duos", 0, 1, 0, true)]
    [InlineData("Other", 4, 0, 1, true)]
    [InlineData("Solos", 0, 1, 0, false)]
    [InlineData("Solos", 0, 1, 2, false)]
    [InlineData("FFA", 0, 1, 4, true)]
    [InlineData("Solos", 0, 4, 1, true)]
    public async Task APlayerMovesToAnyTeamTheStyleHasRoomIn(string style, int leaderTeam, int from, int to, bool moves)
    {
        if (_redis is null)
        {
            return;
        }

        var service = Service();
        await SeedAsync(style, (leaderTeam, Leader, Player(Leader, "2026-10-01T10:00:00.000Z")), (from, Guest, Player(Guest, "2026-10-01T10:00:01.000Z")));

        var answer = await service.AnswerAsync("switch_custom_game_lobby_team", Asking(Guest, new JsonObject { ["MatchID"] = Lobby, ["TeamIndex"] = to }));

        var lobby = await StoredAsync();
        Assert.Equal(moves ? to : from, Members(lobby).Single(m => m.Id == Guest).Team);
        Assert.Equal(2, Counted(lobby));
        if (!moves)
        {
            // Refused as the TS server refused: {} with no envelope.
            Assert.Empty(answer);
        }
    }

    [Fact]
    // The TS server rewrote the whole lobby outside any script for a loadout lock: a ready landing in between was lost.
    public async Task ALoadoutLockNeverLosesAReadyMadeAtTheSameTime()
    {
        if (_redis is null)
        {
            return;
        }

        var service = Service();
        var lock_ = new JsonObject { ["LobbyId"] = Lobby, ["Loadout"] = new JsonObject { ["Character"] = "character_taz", ["Skin"] = "skin_taz_default" } };
        // The ready starts a little after the lock, by a different amount each round, so that some land between a
        // read and a write of the lock (a non-atomic lock loses them: checked by mutation).
        for (int i = 0; i < 200; i++)
        {
            await SeedAsync("Duos", (0, Leader, Player(Leader, "2026-10-01T10:00:00.000Z")), (0, Guest, Player(Guest, "2026-10-01T10:00:01.000Z")));
            int spin = Random.Shared.Next(0, 40000);
            await Task.WhenAll(
                service.SharedAsync("lock_lobby_loadout", Asking(Guest, (JsonObject)lock_.DeepClone()), Lobby),
                Task.Run(async () =>
                {
                    Thread.SpinWait(spin);
                    await service.SharedAsync("set_ready_for_lobby", Asking(Guest, new JsonObject { ["Ready"] = true }), Lobby);
                }));

            var lobby = await StoredAsync();
            Assert.True(lobby["ReadyPlayers"]?[Guest]?.GetValue<bool>(), $"ready lost on round {i}");
            Assert.Equal("character_taz", lobby["LockedLoadouts"]![Guest]!["Character"]!.GetValue<string>());
        }
    }

    [Fact]
    // Player indexes as the TS server gives them (the rollback server depends on these): players before bots within a
    // team, index = place * 2 + team, the first player hosts, spectators 8888+ on team -1, not in the ticket.
    public async Task TheStartHandsTheLauncherTheTsServersPlayerIndexes()
    {
        if (_redis is null)
        {
            return;
        }

        var launcher = new Launcher();
        await SeedAsync("Duos",
            (0, Bot, Player(Bot, "2026-10-01T10:00:00.500Z", "Hard")),
            (0, Leader, Player(Leader, "2026-10-01T10:00:00.000Z")),
            (1, Guest, Player(Guest, "2026-10-01T10:00:01.000Z")),
            (4, Third, Player(Third, "2026-10-01T10:00:02.000Z")));
        await Db.HashSetAsync($"connections:{Guest}", [new("current_ip", "198.51.100.9"), new("character", "character_taz"), new("skin", "skin_taz_default")]);
        // The leader locked Wonder Woman; the session still has the character of before.
        await Db.HashSetAsync($"player:{Leader}", [new("character", "character_wonder_woman"), new("skin", "skin_c001_s01")]);
        await Db.HashSetAsync($"connections:{Leader}", [new("character", "character_shaggy"), new("skin", "skin_shaggy_default")]);

        var answer = await Service(launcher).AnswerAsync("start_custom_match", Asking(Leader, new JsonObject { ["LobbyId"] = Lobby }));

        Assert.Equal(0, answer["return_code"]!.GetValue<int>());
        var launch = launcher.Launched!;
        Assert.Equal(("2v2", "M001"), (launch.Mode, launch.Map));
        Assert.Equal(
            [new MatchPlayer(Leader, 0, 0, true, "", false), new MatchPlayer(Bot, 2, 0, false, "", true), new MatchPlayer(Guest, 1, 1, false, "198.51.100.9", false),
             new MatchPlayer(Third, 8888, -1, false, "", false, IsSpectator: true)],
            launch.Players);
        Assert.Equal(4, launch.BotPerks!.Count);
        Assert.True(launch.NotificationFields!["isCustomGame"]!.GetValue<bool>());
        Assert.True(launch.NotificationFields["customShields"]!.GetValue<bool>());
        Assert.Equal("3", (string?)await Db.HashGetAsync($"bot_config:{Bot}", "difficultyMin"));
        Assert.Equal(Lobby, (string?)await Db.StringGetAsync("ssc_custom_lobby_match:0000000000000000000e0900"));
        Assert.Equal(Lobby, (string?)await Db.StringGetAsync($"ssc_custom_lobby_player:{Third}"));
        // The loadout the websocket's match config reads.
        Assert.Equal("character_taz", (string?)await Db.HashGetAsync($"connections:{Guest}", "character"));
        Assert.Equal("character_wonder_woman", (string?)await Db.HashGetAsync($"connections:{Leader}", "character"));
    }

    [Fact]
    // The rollback server has one input slot per player (bots are not registered) and indexes them by player index: in
    // FFA every player is their own team (index = team), so two players with bots between them got 1 and 3 of 2 slots.
    // Players first, then bots, each in their index order; the walk's order, teams and host stay.
    public void AnFfaWithBotsBetweenThePlayersNumbersThePlayersFirst()
    {
        const string Bot2 = "Bot0000000000000000000e00000000002";
        var entries = new[]
        {
            new MatchPlayer(Bot, 0, 0, false, "", true), new MatchPlayer(Leader, 1, 1, true, "198.51.100.7", false),
            new MatchPlayer(Bot2, 2, 2, false, "", true), new MatchPlayer(Guest, 3, 3, false, "198.51.100.9", false),
        };

        Assert.Equal(
            [new MatchPlayer(Bot, 2, 0, false, "", true), new MatchPlayer(Leader, 0, 1, true, "198.51.100.7", false),
             new MatchPlayer(Bot2, 3, 2, false, "", true), new MatchPlayer(Guest, 1, 3, false, "198.51.100.9", false)],
            CustomLobbyService.HumansFirst(entries));
    }

    [Fact]
    // Two players on one team against two bots got 0 and 2 (place * 2 + team) of 2 slots.
    public void A2v2OfPlayersAgainstBotsNumbersThePlayersFirst()
    {
        const string Bot2 = "Bot0000000000000000000e00000000002";
        var entries = new[]
        {
            new MatchPlayer(Leader, 0, 0, true, "", false), new MatchPlayer(Guest, 2, 0, false, "", false),
            new MatchPlayer(Bot, 1, 1, false, "", true), new MatchPlayer(Bot2, 3, 1, false, "", true),
        };

        Assert.Equal(
            [new MatchPlayer(Leader, 0, 0, true, "", false), new MatchPlayer(Guest, 1, 0, false, "", false),
             new MatchPlayer(Bot, 2, 1, false, "", true), new MatchPlayer(Bot2, 3, 1, false, "", true)],
            CustomLobbyService.HumansFirst(entries));
    }

    [Theory]
    // Every player already below the player count: the TS server's indexes are kept as they are.
    [InlineData(false)]
    [InlineData(true)]
    public void PlayersAlreadyBelowThePlayerCountKeepTheirIndexes(bool withBot)
    {
        var entries = withBot
            // A player and a bot against a player (the start test's lobby): 0 and 1 of 2 slots, the bot 2.
            ? new[] { new MatchPlayer(Leader, 0, 0, true, "", false), new MatchPlayer(Bot, 2, 0, false, "", true), new MatchPlayer(Guest, 1, 1, false, "", false) }
            // Four players in 2v2.
            : [new MatchPlayer(Leader, 0, 0, true, "", false), new MatchPlayer(Guest, 2, 0, false, "", false),
               new MatchPlayer(Third, 1, 1, false, "", false), new MatchPlayer("0000000000000000000e0004", 3, 1, false, "", false)];

        Assert.Null(CustomLobbyService.HumansFirst(entries));
    }

    [Fact]
    public async Task ARematchNeedsAPlayerLeftInTheTeams()
    {
        if (_redis is null)
        {
            return;
        }

        // Everyone else left; the leader watches; bots alone in the teams. The TS server started this match, and the
        // game waited for it forever.
        var launcher = new Launcher();
        await SeedAsync("Duos",
            (0, Bot, Player(Bot, "2026-10-01T10:00:00.500Z", "Hard")),
            (4, Leader, Player(Leader, "2026-10-01T10:00:00.000Z")));

        Assert.False(await Service(launcher).RematchAsync(Lobby));
        Assert.Null(launcher.Launched);

        await SeedAsync("Duos",
            (0, Bot, Player(Bot, "2026-10-01T10:00:00.500Z", "Hard")),
            (1, Leader, Player(Leader, "2026-10-01T10:00:00.000Z")));
        Assert.True(await Service(launcher).RematchAsync(Lobby));
        Assert.NotNull(launcher.Launched);
    }

    [Fact]
    public async Task ALobbyCodeNamesTheLobbyInAnyCase()
    {
        if (_redis is null)
        {
            return;
        }

        var service = Service();
        await SeedAsync("Duos", (0, Leader, Player(Leader, "2026-10-01T10:00:00.000Z")));

        string code = (await service.AnswerAsync("lobby_code", Asking(Leader, new JsonObject { ["LobbyId"] = Lobby })))["body"]!["LobbyCode"]!.GetValue<string>();
        try
        {
            Assert.Matches("^[A-HJ-NP-Z2-9]{5}$", code);
            Assert.Equal(code, (await StoredAsync())["LobbyCode"]!.GetValue<string>());
            var match = await service.ByCodeAsync(code.ToLowerInvariant());
            Assert.Equal(Lobby, match!["id"]!.GetValue<string>());
            Assert.Equal("custom_game_lobby", match["template"]!["slug"]!.GetValue<string>());
            Assert.Null(await service.ByCodeAsync("ZZZZZZZZZZZ"));
            // Only the leader gets one.
            Assert.Null((await service.AnswerAsync("lobby_code", Asking(Guest, new JsonObject { ["LobbyId"] = Lobby })))["body"]!["LobbyCode"]);
        }
        finally
        {
            await Db.KeyDeleteAsync($"lobby_code:{code}");
        }
    }

    [Fact]
    // cjson writes {} and [] alike: the fields that hold maps are objects again, those that hold lists arrays, the
    // rest left as they came.
    public void CjsonsEmptyTablesArePutBack()
    {
        var node = JsonNode.Parse("""{"Players":[],"Maps":{},"Other":[],"Teams":[{"Players":[],"Length":0}],"lobby":{"WorldBuffs":{}}}""");

        var fixedUp = CustomLobbyService.FixEmptyTables(node)!;

        Assert.Equal("""{"Players":{},"Maps":[],"Other":[],"Teams":[{"Players":{},"Length":0}],"lobby":{"WorldBuffs":[]}}""", Js.Stringify(fixedUp));
    }

    [Fact]
    public void AGameModeGivesItsSettingsAndAModeWithoutTeamsCannotBeSet()
    {
        var settings = GameModes.DefaultSettings("gm_infinitejumps");
        Assert.Equal("""{"TeamStyle":"Duos","QueueType":"Unselected","Context":"Custom","ModeDifficulty":"Unselected","GameModeAlias":"Versus","NumRingoutsForWin":4,"MatchDuration":420,"AllowHazards":true,"AllowDuplicateCharacters":true,"AreRewardsSkipped":true,"num_set_wins_required":1,"EnableShields":1}""",
            Js.Stringify(settings["match_config"]));
        Assert.Equal("""{"teamBuffs":["buff_infinite_jumps","buff_low_gravity"],"players":{}}""", Js.Stringify(GameModes.BuffMatrix("gm_infinitejumps")["0"]));
        Assert.All(settings["Maps"]!.AsArray(), m => Assert.True(m!["IsSelected"]!.GetValue<bool>()));
        Assert.Throws<UnknownGameModeException>(() => GameModes.DefaultSettings("classic_game_mode"));
        Assert.Throws<UnknownGameModeException>(() => GameModes.DefaultSettings("gm_not_a_mode"));
    }
}

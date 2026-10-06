using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.CustomLobbies;
using OpenVersus.Server.Core.Matches;
using OpenVersus.Server.Core.Realtime;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Tests.Realtime;

/// <summary>
/// The lobbies' reader of the gateway's disconnects (LobbyDisconnects) on real Redis, database 15
/// (OVS_TEST_REDIS[_USER/_PW]): which events it acts on, when it does nothing, and the post-match window. What the party
/// and the custom lobby do with a disconnect is PartyServiceTests' and CustomLobbyServiceTests'; parity with the TS
/// websocket's close is tools/realtime/disconnect_diff.mjs.
/// </summary>
[Collection(RedisTestDatabase.Name)]
public sealed class LobbyDisconnectsTests : IAsyncLifetime
{
    private static readonly string? s_redis = Environment.GetEnvironmentVariable("OVS_TEST_REDIS");
    private ConnectionMultiplexer? _redis;

    // Ids no real player has; every key a test makes holds one of them.
    private const string Player = "0000000000000000000e0001", Partner = "0000000000000000000e0002", Connection = "0000000000000000000e0c01";
    private const string Queue = "2v2";
    private const string Stream = "realtime:connections:0000000000000000000e", Due = "realtime:disconnects:due:0000000000000000000e";
    private const string Token = "the-session-token";

    // What the reader asked of the lobbies, in order.
    private sealed class Lobbies : IPartyService, ICustomLobbyService
    {
        public List<string> Calls { get; } = [];
        public int FailuresLeft { get; set; }

        private Task Record(string call)
        {
            if (FailuresLeft > 0)
            {
                FailuresLeft--;
                throw new TimeoutException("test");
            }

            Calls.Add(call);
            return Task.CompletedTask;
        }

        Task IPartyService.PlayerDisconnectedAsync(string playerId) => Record($"party {playerId}");
        public Task ForgetLobbyAsync(string playerId) => Record($"forget {playerId}");
        Task ICustomLobbyService.PlayerDisconnectedAsync(string playerId) => Record($"custom {playerId}");

        public Task<string?> CustomLobbyAsync(string route, PartyRequest request) => throw new NotSupportedException();
        public Task<JsonObject> CreatePartyLobbyAsync(PartyRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<JsonObject> CreatePartyAsync(PartyRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<JsonObject> SetModeAsync(PartyRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<JsonObject> InviteAsync(PartyRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<JsonObject> JoinAsync(PartyRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<JsonObject> LeaveAsync(PartyRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<JsonObject> SetNotJoinableAsync(PartyRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<JsonObject> SetReadyAsync(PartyRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<JsonObject> LockLoadoutAsync(PartyRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<JsonObject> AnswerAsync(string route, PartyRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<JsonObject?> SharedAsync(string route, PartyRequest request, string lobbyId, CancellationToken ct) => throw new NotSupportedException();
        public Task<JsonObject?> ByCodeAsync(string code, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> RematchAsync(string lobbyId, CancellationToken ct) => throw new NotSupportedException();
    }

    private readonly Lobbies _lobbies = new();
    private static readonly string[] s_cleaned = [$"party {Player}", $"custom {Player}", $"forget {Player}"];

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
        foreach (var key in server.Keys(15, "*0000000000000000000e*"))
        {
            await _redis.GetDatabase().KeyDeleteAsync(key);
        }

        await _redis.GetDatabase().SetRemoveAsync(GatewayPresence.OnlinePlayers, Player);
        await _redis.GetDatabase().HashDeleteAsync(MatchmakingQueue.QueuedKey, [Player, Partner]);
        foreach (var raw in await _redis.GetDatabase().ListRangeAsync(Queue))
        {
            if (raw.ToString().Contains("0000000000000000000e", StringComparison.Ordinal))
            {
                await _redis.GetDatabase().ListRemoveAsync(Queue, raw);
            }
        }
    }

    private IDatabase Db => _redis!.GetDatabase();

    private LobbyDisconnects Reader() => new(new ServiceCollection().AddSingleton<IConnectionMultiplexer>(_redis!).BuildServiceProvider(),
        _lobbies, _lobbies, TimeProvider.System, NullLogger<LobbyDisconnects>.Instance)
    { StreamKey = Stream, DueKey = Due };

    // A close now; a ticket queued a minute before it.
    private static readonly long s_closedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static Disconnect Closed(string token = Token) => new(Player, Connection, GatewayPresence.TokenHash(token), s_closedAt);

    // As the gateway appends it (GatewayPresence.ClosedAsync), for the session /access left.
    private Task AppendAsync(string type) => Db.StreamAddAsync(Stream,
        [new("type", type), new("player", Player), new("connection", Connection), new("at", s_closedAt), new("node", "test"), new("ip", ""), new("token", GatewayPresence.TokenHash(Token))]);

    // The player and their partner queued as a party, a minute before the close (MatchmakingQueue.QueueAsync).
    private async Task<string> QueuedAsync()
    {
        string ticket = Js.Stringify(new JsonObject
        {
            ["created_at"] = s_closedAt / 1000 - 60,
            ["matchType"] = Queue,
            ["partyLeaderId"] = Player,
            ["matchmakingRequestId"] = "0000000000000000000e0800",
            ["players"] = new JsonArray(new JsonObject { ["id"] = Player }, new JsonObject { ["id"] = Partner }),
        });
        await Db.ListRightPushAsync(Queue, ticket);
        await Db.HashSetAsync(MatchmakingQueue.QueuedKey, [new(Player, ticket), new(Partner, ticket)]);
        return ticket;
    }

    private async Task<bool> TicketGoneAsync() =>
        !await Db.HashExistsAsync(MatchmakingQueue.QueuedKey, Player) && !await Db.HashExistsAsync(MatchmakingQueue.QueuedKey, Partner)
        && !(await Db.ListRangeAsync(Queue)).Any(t => t.ToString().Contains(Player, StringComparison.Ordinal));

    private Task SessionAsync(string token = Token) => Db.HashSetAsync($"connections:{Player}", "jwt", token);

    [Fact]
    // The group starts at the stream's end: a disconnect appended before it existed (the gateway runs before the lobbies
    // reader is deployed) is never replayed against a player who may be back. So again when the group is lost.
    public async Task OnlyDisconnectsAppendedOnceTheGroupExistsAreHandled()
    {
        if (_redis is null)
        {
            return;
        }

        var reader = Reader();
        await SessionAsync();
        await AppendAsync("disconnected");
        await reader.EnsureGroupAsync(Db);
        Assert.Equal(0, await reader.ReadAsync(Db));
        Assert.Empty(_lobbies.Calls);

        await AppendAsync("connected");
        await AppendAsync("disconnected");
        Assert.Equal(2, await reader.ReadAsync(Db));
        Assert.Equal(s_cleaned, _lobbies.Calls);
        Assert.Equal(0, (await Db.StreamPendingAsync(Stream, LobbyDisconnects.Group)).PendingMessageCount);

        await Db.StreamDeleteConsumerGroupAsync(Stream, LobbyDisconnects.Group);
        await AppendAsync("disconnected");
        Assert.Equal(0, await reader.ReadAsync(Db));
        await AppendAsync("disconnected");
        Assert.Equal(1, await reader.ReadAsync(Db));
        Assert.Equal(s_cleaned.Concat(s_cleaned), _lobbies.Calls);
    }

    [Fact]
    // The party, the custom lobby, the player's own lobby, as the TS close did them; then online_players, which the
    // gateway keeps through a post-match window that may have closed since.
    public async Task ADisconnectCleansUpTheLobbiesInTheTsOrderAndTakesThePlayerOffline()
    {
        if (_redis is null)
        {
            return;
        }

        await SessionAsync();
        await Db.SetAddAsync(GatewayPresence.OnlinePlayers, Player);

        Assert.Equal(LobbyDisconnects.Outcome.Done, await Reader().DisconnectedAsync(Db, Closed()));

        Assert.Equal(s_cleaned, _lobbies.Calls);
        Assert.False(await Db.SetContainsAsync(GatewayPresence.OnlinePlayers, Player));
    }

    [Fact]
    // The gateway deletes realtime:conn:{player} when the current connection closes: an entry is a newer connection.
    public async Task NothingIsDoneForAPlayerWhoConnectedAgain()
    {
        if (_redis is null)
        {
            return;
        }

        await SessionAsync();
        await Db.HashSetAsync(GatewayPresence.ConnectionKey(Player), "id", "0000000000000000000e0c02");
        await Db.SetAddAsync(GatewayPresence.OnlinePlayers, Player);

        Assert.Equal(LobbyDisconnects.Outcome.Back, await Reader().DisconnectedAsync(Db, Closed()));

        Assert.Empty(_lobbies.Calls);
        Assert.True(await Db.SetContainsAsync(GatewayPresence.OnlinePlayers, Player));
    }

    [Fact]
    // /access comes before the websocket: a login since the closed connection's (another token in the session) must not
    // lose the lobby that login just made. The same event with the session it was opened with is handled.
    public async Task NothingIsDoneForAPlayerWhoLoggedInAgain()
    {
        if (_redis is null)
        {
            return;
        }

        await SessionAsync("a-newer-session-token");
        Assert.Equal(LobbyDisconnects.Outcome.Back, await Reader().DisconnectedAsync(Db, Closed()));
        Assert.Empty(_lobbies.Calls);

        await SessionAsync();
        Assert.Equal(LobbyDisconnects.Outcome.Done, await Reader().DisconnectedAsync(Db, Closed()));
        Assert.Equal(s_cleaned, _lobbies.Calls);
    }

    [Fact]
    // The post-match window keeps the party for a player who comes back in it: their disconnect waits for its end, then
    // is handled as any other (the gateway kept them in online_players for the window).
    public async Task ADisconnectInThePostMatchWindowIsHandledWhenItEnds()
    {
        if (_redis is null)
        {
            return;
        }

        var reader = Reader();
        await SessionAsync();
        await Db.SetAddAsync(GatewayPresence.OnlinePlayers, Player);
        await Db.StringSetAsync($"rejoin_pending:{Player}", "1", TimeSpan.FromSeconds(45));

        Assert.Equal(LobbyDisconnects.Outcome.Deferred, await reader.DisconnectedAsync(Db, Closed()));
        Assert.Empty(_lobbies.Calls);
        var deferred = Assert.Single(await Db.SortedSetRangeByScoreWithScoresAsync(Due));
        double inSeconds = (deferred.Score - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) / 1000;
        Assert.InRange(inSeconds, 40, 45);
        Assert.Equal(0, await reader.SweepAsync(Db));

        // The window ends.
        await Db.KeyDeleteAsync($"rejoin_pending:{Player}");
        await Db.SortedSetAddAsync(Due, deferred.Element, 0);
        Assert.Equal(1, await reader.SweepAsync(Db));

        Assert.Equal(s_cleaned, _lobbies.Calls);
        Assert.False(await Db.SetContainsAsync(GatewayPresence.OnlinePlayers, Player));
        Assert.Equal(0, await Db.SortedSetLengthAsync(Due));
    }

    [Fact]
    // A player who came back in the window keeps everything: their deferred disconnect is dropped when it comes due.
    public async Task ADeferredDisconnectOfAPlayerWhoCameBackIsDropped()
    {
        if (_redis is null)
        {
            return;
        }

        var reader = Reader();
        await SessionAsync();
        await Db.SortedSetAddAsync(Due, Closed().ToJson(), 0);
        await SessionAsync("a-newer-session-token");
        await Db.SetAddAsync(GatewayPresence.OnlinePlayers, Player);

        Assert.Equal(1, await reader.SweepAsync(Db));

        Assert.Empty(_lobbies.Calls);
        Assert.True(await Db.SetContainsAsync(GatewayPresence.OnlinePlayers, Player));
        Assert.Equal(0, await Db.SortedSetLengthAsync(Due));
    }

    [Fact]
    // A deferred disconnect taken off the set and failing is put back for later, not lost.
    public async Task ADeferredDisconnectThatFailsIsTriedAgain()
    {
        if (_redis is null)
        {
            return;
        }

        var reader = Reader();
        await SessionAsync();
        await Db.SortedSetAddAsync(Due, Closed().ToJson(), 0);
        _lobbies.FailuresLeft = 1;

        Assert.Equal(1, await reader.SweepAsync(Db));

        var again = Assert.Single(await Db.SortedSetRangeByScoreWithScoresAsync(Due));
        Assert.Equal(1, Disconnect.FromJson(again.Element!)!.Failures);
        Assert.True(again.Score > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await Db.SortedSetAddAsync(Due, again.Element, 0);
        Assert.Equal(1, await reader.SweepAsync(Db));
        Assert.Equal(s_cleaned, _lobbies.Calls);
    }

    [Fact]
    // A newer login took the connection over (the game was relaunched): its old ticket goes, as the TS websocket's
    // handshake dropped it; the lobbies are the new login's.
    public async Task AReplacedConnectionDropsItsTicketAndNothingElse()
    {
        if (_redis is null)
        {
            return;
        }

        var reader = Reader();
        await reader.EnsureGroupAsync(Db);
        await QueuedAsync();

        await AppendAsync("replaced");
        Assert.Equal(1, await reader.ReadAsync(Db));

        Assert.True(await TicketGoneAsync());
        Assert.Empty(_lobbies.Calls);
    }

    [Fact]
    // The lobbies stay with a player who came back, but not the search their old game was in: a login starts at the
    // title. So too in the post-match window, where the lobbies wait.
    public async Task TheTicketGoesEvenWhenTheLobbiesStay()
    {
        if (_redis is null)
        {
            return;
        }

        await SessionAsync("a-newer-session-token");
        await QueuedAsync();
        Assert.Equal(LobbyDisconnects.Outcome.Back, await Reader().DisconnectedAsync(Db, Closed()));
        Assert.True(await TicketGoneAsync());

        await SessionAsync();
        await QueuedAsync();
        await Db.StringSetAsync($"rejoin_pending:{Player}", "1", TimeSpan.FromSeconds(45));
        Assert.Equal(LobbyDisconnects.Outcome.Deferred, await Reader().DisconnectedAsync(Db, Closed()));
        Assert.True(await TicketGoneAsync());
        Assert.Empty(_lobbies.Calls);
    }
}

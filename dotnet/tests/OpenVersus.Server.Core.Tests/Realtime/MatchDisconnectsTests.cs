using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenVersus.Server.Core.Matches;
using OpenVersus.Server.Core.Realtime;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Tests.Realtime;

/// <summary>
/// The match flow's reader of the gateway's disconnects (MatchDisconnects) on real Redis, database 15
/// (OVS_TEST_REDIS[_USER/_PW]): which events reach the match (IMatchStatusEvents.GameClosedAsync, whose work is
/// MatchStatusEventsTests'), and that a player who came back is left to the rollback server.
/// </summary>
[Collection(RedisTestDatabase.Name)]
public sealed class MatchDisconnectsTests : IAsyncLifetime
{
    private static readonly string? s_redis = Environment.GetEnvironmentVariable("OVS_TEST_REDIS");
    private ConnectionMultiplexer? _redis;

    private const string Player = "0000000000000000002e0001", Connection = "0000000000000000002e0c01";
    private const string Stream = "realtime:connections:0000000000000000002e";
    private const string Token = "the-session-token";

    private sealed class Events : IMatchStatusEvents
    {
        public List<string> Closed { get; } = [];

        public Task GameClosedAsync(string playerId)
        {
            Closed.Add(playerId);
            return Task.CompletedTask;
        }

        public Task<(int Status, JsonObject Answer)> HandleAsync(string? matchUpdateKey, JsonNode? body, string? from) => throw new NotSupportedException();
    }

    private readonly Events _events = new();

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
        foreach (var key in server.Keys(15, "*0000000000000000002e*"))
        {
            await _redis.GetDatabase().KeyDeleteAsync(key);
        }
    }

    private IDatabase Db => _redis!.GetDatabase();

    private MatchDisconnects Reader() => new(new ServiceCollection().AddSingleton<IConnectionMultiplexer>(_redis!).BuildServiceProvider(),
        _events, TimeProvider.System, NullLogger<MatchDisconnects>.Instance)
    { StreamKey = Stream };

    private Task AppendAsync(string type) => Db.StreamAddAsync(Stream,
        [new("type", type), new("player", Player), new("connection", Connection), new("at", 1), new("node", "test"), new("ip", ""), new("token", GatewayPresence.TokenHash(Token))]);

    [Fact]
    // Only a disconnect reaches the match, and only from a player who has not come back: one who connected or logged in
    // again is the rollback server's to call (a crash or a dodge).
    public async Task OnlyTheDisconnectOfAPlayerWhoIsStillGoneReachesTheMatch()
    {
        if (_redis is null)
        {
            return;
        }

        var reader = Reader();
        await Db.HashSetAsync($"connections:{Player}", "jwt", Token);
        await reader.EnsureGroupAsync(Db);

        await AppendAsync("connected");
        await AppendAsync("replaced");
        await AppendAsync("disconnected");
        Assert.Equal(3, await reader.ReadAsync(Db));
        Assert.Equal([Player], _events.Closed);

        // Logged in again.
        await Db.HashSetAsync($"connections:{Player}", "jwt", "a-newer-session-token");
        await AppendAsync("disconnected");
        Assert.Equal(1, await reader.ReadAsync(Db));
        Assert.Equal([Player], _events.Closed);

        // Connected again.
        await Db.HashSetAsync($"connections:{Player}", "jwt", Token);
        await Db.HashSetAsync(GatewayPresence.ConnectionKey(Player), "id", "0000000000000000002e0c02");
        await AppendAsync("disconnected");
        Assert.Equal(1, await reader.ReadAsync(Db));
        Assert.Equal([Player], _events.Closed);
    }
}

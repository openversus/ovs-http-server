using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Perks;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Tests.Perks;

/// <summary>perks_lock (<see cref="IPerksLock"/>). Real Redis, database 15 (OVS_TEST_REDIS).</summary>
[Collection(RedisTestDatabase.Name)]
public sealed class PerksLockTests : IAsyncLifetime
{
    private static readonly string? s_redis = Environment.GetEnvironmentVariable("OVS_TEST_REDIS");
    private const string Match = "0000000000000000000c0100";
    private const string Me = "0000000000000000000c0001";
    private const string Other = "0000000000000000000c0002";
    private const string Third = "0000000000000000000c0003";
    private const string Bot = "Bot0000000000000000000c0000000004";

    private ConnectionMultiplexer? _redis;
    private readonly ConcurrentQueue<string> _published = new();

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
        // Channels ignore the database: only this class's match counts.
        await _redis.GetSubscriber().SubscribeAsync(RedisChannel.Literal(PerksLock.Channel), (_, m) =>
        {
            if (m.ToString().Contains(Match, StringComparison.Ordinal))
            {
                _published.Enqueue(m.ToString());
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

    private IDatabase Db => _redis!.GetDatabase();

    private async Task CleanAsync()
    {
        var server = _redis!.GetServer(_redis.GetEndPoints()[0]);
        foreach (var key in server.Keys(15, $"*{Match}*"))
        {
            await Db.KeyDeleteAsync(key);
        }
    }

    private IPerksLock Lock() => new PerksLock(new ServiceCollection().AddSingleton<IConnectionMultiplexer>(_redis!).BuildServiceProvider(), NullLogger<PerksLock>.Instance);

    // match:{match} as the TS matchmaker and MatchLauncher write it: tickets of players.
    private Task MatchAsync(params string[][] tickets) => Db.StringSetAsync($"match:{Match}", Js.Stringify(new JsonObject
    {
        ["matchId"] = Match,
        ["tickets"] = new JsonArray([.. tickets.Select(t => (JsonNode)new JsonObject
        {
            ["players"] = new JsonArray([.. t.Select(p => (JsonNode)new JsonObject { ["id"] = p, ["skill"] = 0, ["region"] = "local" })]),
        })]),
        ["status"] = "pending",
    }));

    private static JsonObject Body(JsonNode? perks) => new() { ["ContainerMatchId"] = Match, ["Perks"] = perks };

    private async Task<List<string>> PublishedAsync()
    {
        await Task.Delay(200);
        return [.. _published];
    }

    [SkippableFact]
    public async Task TheLastPlayerToLockPublishesEveryTicketPlayerInTicketOrder()
    {
        Skip.If(string.IsNullOrEmpty(s_redis), "set OVS_TEST_REDIS to run");
        await MatchAsync([Other], [Me, Third]);
        await Lock().LockAsync(Me, Body(new JsonArray("perk_a", "perk_b")), default);
        await Lock().LockAsync(Third, Body(new JsonArray()), default);
        Assert.Empty(await PublishedAsync());

        await Lock().LockAsync(Other, Body(new JsonArray("perk_c")), default);
        Assert.Equal([$$"""{"containerMatchId":"{{Match}}","playerIds":["{{Other}}","{{Me}}","{{Third}}"]}"""], await PublishedAsync());
        // Stored as sent (JSON.stringify), for 20 minutes.
        Assert.Equal("""["perk_a","perk_b"]""", (string?)await Db.StringGetAsync($"match:{Match}:perks:{Me}"));
        var ttl = await Db.KeyTimeToLiveAsync($"match:{Match}:perks:{Me}");
        Assert.InRange(ttl!.Value.TotalSeconds, 1190, 1200);
    }

    [SkippableFact]
    public async Task BotsLockedAtLaunchCountAsLocked()
    {
        Skip.If(string.IsNullOrEmpty(s_redis), "set OVS_TEST_REDIS to run");
        await MatchAsync([Me, Bot]);
        await Db.StringSetAsync($"match:{Match}:perks:{Bot}", "[]");
        await Lock().LockAsync(Me, Body(new JsonArray("perk_a")), default);
        Assert.Single(await PublishedAsync());
    }

    [SkippableFact]
    // TS published again for a second lock that saw everyone locked (two players at the same moment); here once.
    public async Task PublishedOncePerMatch()
    {
        Skip.If(string.IsNullOrEmpty(s_redis), "set OVS_TEST_REDIS to run");
        await MatchAsync([Me], [Other]);
        await Lock().LockAsync(Me, Body(new JsonArray("perk_a")), default);
        await Lock().LockAsync(Other, Body(new JsonArray("perk_b")), default);
        await Lock().LockAsync(Me, Body(new JsonArray("perk_a")), default);
        Assert.Single(await PublishedAsync());
    }

    [SkippableFact]
    // TS: a missing Perks made the Redis write throw and the request was never answered.
    public async Task NoPerksListLocksWithNone()
    {
        Skip.If(string.IsNullOrEmpty(s_redis), "set OVS_TEST_REDIS to run");
        await MatchAsync([Me]);
        await Lock().LockAsync(Me, new JsonObject { ["ContainerMatchId"] = Match }, default);
        Assert.Equal("[]", (string?)await Db.StringGetAsync($"match:{Match}:perks:{Me}"));
        Assert.Single(await PublishedAsync());
    }

    [SkippableFact]
    public async Task AnUnknownMatchStoresThePerksAndPublishesNothing()
    {
        Skip.If(string.IsNullOrEmpty(s_redis), "set OVS_TEST_REDIS to run");
        await Lock().LockAsync(Me, Body(new JsonArray("perk_a")), default);
        Assert.Equal("""["perk_a"]""", (string?)await Db.StringGetAsync($"match:{Match}:perks:{Me}"));
        Assert.Empty(await PublishedAsync());
    }

    [SkippableFact]
    // TS stored match:undefined:perks:{player}.
    public async Task NoContainerMatchIdStoresNothing()
    {
        Skip.If(string.IsNullOrEmpty(s_redis), "set OVS_TEST_REDIS to run");
        await Lock().LockAsync(Me, new JsonObject { ["Perks"] = new JsonArray("perk_a") }, default);
        Assert.False(await Db.KeyExistsAsync($"match:undefined:perks:{Me}"));
        Assert.False(await Db.KeyExistsAsync($"match::perks:{Me}"));
    }
}

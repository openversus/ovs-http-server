using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenVersus.Server.Core.Access;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Realtime;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Tests.Access;

/// <summary>
/// The daily toast bonus's popup at the game's connection (DailyToastPopups) on real Redis, database 15
/// (OVS_TEST_REDIS[_USER/_PW]). The bytes the game is sent are compared with the TS websocket's by
/// tools/realtime/gateway_diff.mjs (daily-toast-bonus).
/// </summary>
[Collection(RedisTestDatabase.Name)]
public sealed class DailyToastPopupsTests : IAsyncLifetime
{
    private static readonly string? s_redis = Environment.GetEnvironmentVariable("OVS_TEST_REDIS");
    private ConnectionMultiplexer? _redis;
    private readonly ConcurrentQueue<JsonObject> _sent = new();

    private const string Player = "0000000000000000003e0001", Connection = "0000000000000000003e0c01";
    private const string Stream = "realtime:connections:0000000000000000003e";
    private static string Flag => $"daily_toast_bonus_pending:{Player}";

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
        await _redis.GetSubscriber().SubscribeAsync(RedisChannel.Literal("ws:send"), (_, message) =>
        {
            if (JsonNode.Parse(message.ToString()) is JsonObject sent && sent["playerIds"]!.ToJsonString().Contains(Player))
            {
                _sent.Enqueue(sent);
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
        var server = _redis!.GetServer(_redis.GetEndPoints()[0]);
        foreach (var key in server.Keys(15, "*0000000000000000003e*"))
        {
            await _redis.GetDatabase().KeyDeleteAsync(key);
        }
    }

    private IDatabase Db => _redis!.GetDatabase();

    private DailyToastPopups Reader() => new(new ServiceCollection().AddSingleton<IConnectionMultiplexer>(_redis!).BuildServiceProvider(),
        TimeProvider.System, NullLogger<DailyToastPopups>.Instance)
    { StreamKey = Stream };

    private Task AppendAsync(string type, string connection = Connection) => Db.StreamAddAsync(Stream,
        [new("type", type), new("player", Player), new("connection", connection), new("at", 1), new("node", "test"), new("ip", ""), new("token", "")]);

    private async Task<List<JsonObject>> SentAsync()
    {
        await Task.Delay(200);
        return [.. _sent.Select(s => (JsonObject)s["message"]!)];
    }

    [Fact]
    // The popup goes once, to the connection that is the player's: one that closed or was replaced leaves the flag to the
    // connection that took over. A count that is not a number above 0 is dropped.
    public async Task TheCurrentConnectionIsShownTheBonusOnce()
    {
        if (_redis is null)
        {
            return;
        }

        var reader = Reader();
        await reader.EnsureGroupAsync(Db);
        await Db.StringSetAsync(Flag, "10", TimeSpan.FromMinutes(5));
        await Db.HashSetAsync(GatewayPresence.ConnectionKey(Player), "id", "0000000000000000003e0c02");

        await AppendAsync("connected");
        await AppendAsync("disconnected", "0000000000000000003e0c02");
        Assert.Equal(2, await reader.ReadAsync(Db));
        Assert.True(await Db.KeyExistsAsync(Flag));
        Assert.Empty(await SentAsync());

        await AppendAsync("connected", "0000000000000000003e0c02");
        await AppendAsync("connected", "0000000000000000003e0c02");
        Assert.Equal(2, await reader.ReadAsync(Db));
        Assert.False(await Db.KeyExistsAsync(Flag));
        var popup = Assert.Single(await SentAsync());
        Assert.Equal(Js.Stringify(DailyToastPopups.Popup(Player, 10)), Js.Stringify(popup));
        Assert.Equal("""{"RewardGuid":"OVS-DAILY-TOAST-BONUS","Constraints":[],"RewardGrantMethod":"DirectInventoryItem","InventoryHsda":"match_toasts","DirectInventoryItemCount":10}""",
            popup["data"]!["RewardsGranted"]![0]!.ToJsonString());

        await Db.StringSetAsync(Flag, "ten", TimeSpan.FromMinutes(5));
        await AppendAsync("connected", "0000000000000000003e0c02");
        Assert.Equal(1, await reader.ReadAsync(Db));
        Assert.False(await Db.KeyExistsAsync(Flag));
        Assert.Single(await SentAsync());
    }
}

using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OpenVersus.Server.Core.Realtime;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Tests.Realtime;

/// <summary>
/// Each player's replay log (PlayerMessages.SendAsync and DisconnectAsync) on real Redis, database 15
/// (OVS_TEST_REDIS[_USER/_PW]): what is appended to realtime:out:{player}, what ws:send and ws:disconnect carry (the
/// payload as it was without the log, the sequence appended), the exact trim to the window and the TTL.
/// </summary>
[Collection(RedisTestDatabase.Name)]
public sealed class PlayerMessagesTests : IAsyncLifetime
{
    private static readonly string? s_redis = Environment.GetEnvironmentVariable("OVS_TEST_REDIS");
    private ConnectionMultiplexer? _redis;

    // Ids no real player has; every key a test makes holds the prefix.
    private const string Prefix = "0000000000000000004f";
    private const string Player = Prefix + "0001", Other = Prefix + "0002";

    // What was published on either channel naming one of these players, in order, as it was published.
    private readonly ConcurrentQueue<(string Channel, string Payload)> _published = new();

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
        foreach (string channel in new[] { GatewayChannels.Send, GatewayChannels.Disconnect })
        {
            await _redis.GetSubscriber().SubscribeAsync(RedisChannel.Literal(channel), (_, m) =>
            {
                if (m.ToString().Contains(Prefix))
                {
                    _published.Enqueue((channel, m.ToString()));
                }
            });
        }
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
        foreach (var key in server.Keys(15, $"*{Prefix}*"))
        {
            await Db.KeyDeleteAsync(key);
        }
    }

    private IDatabase Db => _redis!.GetDatabase();

    /// <summary>A ws:send payload as it was before the replay log: without the seqs appended to it.</summary>
    public static string WithoutSequences(string payload) => Regex.Replace(payload, ""","seqs":\{[^{}]*\}\}$""", "}");

    private async Task<List<(string Channel, string Payload)>> PublishedAsync(int count)
    {
        for (int i = 0; i < 100 && _published.Count < count; i++)
        {
            await Task.Delay(20);
        }

        await Task.Delay(50);
        return [.. _published];
    }

    private async Task<List<StreamEntry>> LogAsync(string player) => [.. await Db.StreamRangeAsync(PlayerMessages.LogKey(player))];

    [Fact]
    // One message to two players: one entry in each log holding the message exactly as published, and ws:send carries the
    // payload it carried before the log with seqs appended, naming each player's entry.
    public async Task AMessageIsLoggedForEachPlayerAndPublishedWithItsSequences()
    {
        if (_redis is null)
        {
            return;
        }

        await PlayerMessages.SendAsync(Db, [Player, Other], new JsonObject { ["cmd"] = "update", ["data"] = new JsonObject { ["2"] = 1, ["1"] = "é/\"" } });

        const string message = """{"cmd":"update","data":{"2":1,"1":"é/\""}}""";
        var mine = await LogAsync(Player);
        var other = await LogAsync(Other);
        var entry = Assert.Single(mine);
        Assert.Equal([new NameValueEntry("message", message)], entry.Values);
        Assert.Equal(message, (string?)Assert.Single(other)["message"]);

        var (channel, payload) = Assert.Single(await PublishedAsync(1));
        Assert.Equal(GatewayChannels.Send, channel);
        string before = $$"""{"playerIds":["{{Player}}","{{Other}}"],"message":{{message}}""";
        Assert.StartsWith(before + ",\"seqs\":{", payload);
        var seqs = Assert.IsType<JsonObject>(JsonNode.Parse(payload)!["seqs"]);
        Assert.Equal(2, seqs.Count);
        Assert.Equal(entry.Id.ToString(), (string?)seqs[Player]);
        Assert.Equal(Assert.Single(other).Id.ToString(), (string?)seqs[Other]);
        Assert.Equal(before + "}", WithoutSequences(payload));

        // The log outlives its newest entry by the TTL.
        var ttl = await Db.KeyTimeToLiveAsync(PlayerMessages.LogKey(Player));
        Assert.InRange(ttl!.Value, PlayerMessages.ReplayTtl - TimeSpan.FromSeconds(5), PlayerMessages.ReplayTtl);
    }

    [Fact]
    // Messages and closes go into one log, in the order they were sent, each sequence higher than the last; a player named
    // twice is logged once (the payload keeps the names as given).
    public async Task MessagesAndClosesShareOneLogInOrder()
    {
        if (_redis is null)
        {
            return;
        }

        await PlayerMessages.SendAsync(Db, [Player, Player], new JsonObject { ["cmd"] = "first" });
        long heard = await PlayerMessages.DisconnectAsync(Db, new JsonObject { ["playerId"] = Player, ["code"] = 1000, ["reason"] = "replaced" });
        await PlayerMessages.SendAsync(Db, [Player], new JsonObject { ["cmd"] = "second" });

        var log = await LogAsync(Player);
        Assert.Equal(["message", "disconnect", "message"], log.Select(e => e.Values.Single().Name.ToString()));
        Assert.Equal(["""{"cmd":"first"}""", $$"""{"playerId":"{{Player}}","code":1000,"reason":"replaced"}""", """{"cmd":"second"}"""],
            log.Select(e => e.Values.Single().Value.ToString()));

        var published = await PublishedAsync(3);
        Assert.Equal([GatewayChannels.Send, GatewayChannels.Disconnect, GatewayChannels.Send], published.Select(p => p.Channel));
        Assert.Equal($$$"""{"playerIds":["{{{Player}}}","{{{Player}}}"],"message":{"cmd":"first"},"seqs":{"{{{Player}}}":"{{{log[0].Id}}}"}}""", published[0].Payload);
        Assert.Equal($$"""{"playerId":"{{Player}}","code":1000,"reason":"replaced","seq":"{{log[1].Id}}"}""", published[1].Payload);
        Assert.Equal($$$"""{"playerIds":["{{{Player}}}"],"message":{"cmd":"second"},"seqs":{"{{{Player}}}":"{{{log[2].Id}}}"}}""", published[2].Payload);
        Assert.True(heard >= 1);
    }

    [Fact]
    // The log keeps exactly the window, by the Redis clock (the one its ids come from): a send drops an entry just older
    // than the window and keeps one just inside it.
    public async Task ASendTrimsTheLogToTheWindowExactly()
    {
        if (_redis is null)
        {
            return;
        }

        var server = _redis.GetServer(_redis.GetEndPoints()[0]);
        long now = new DateTimeOffset(DateTime.SpecifyKind(await server.TimeAsync(), DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        long window = (long)PlayerMessages.ReplayWindow.TotalMilliseconds;
        string key = PlayerMessages.LogKey(Player);
        await Db.StreamAddAsync(key, "message", "old", $"{now - window - 2_000}-0");
        await Db.StreamAddAsync(key, "message", "kept", $"{now - window + 10_000}-0");

        await PlayerMessages.SendAsync(Db, [Player], new JsonObject { ["cmd"] = "new" });

        Assert.Equal(["kept", """{"cmd":"new"}"""], (await LogAsync(Player)).Select(e => e["message"].ToString()));
    }

    [Fact]
    // A close request that names no player (nothing to log it under) is published as it is.
    public async Task ACloseWithoutAPlayerIsPublishedUnlogged()
    {
        if (_redis is null)
        {
            return;
        }

        await PlayerMessages.DisconnectAsync(Db, new JsonObject { ["connectionId"] = Prefix + "0c01" });

        var (channel, payload) = Assert.Single(await PublishedAsync(1));
        Assert.Equal(GatewayChannels.Disconnect, channel);
        Assert.Equal($$"""{"connectionId":"{{Prefix}}0c01"}""", payload);
        var server = _redis.GetServer(_redis.GetEndPoints()[0]);
        Assert.Empty(server.Keys(15, $"realtime:out:*{Prefix}*"));
    }
}

using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenVersus.Server.Core.Access;
using OpenVersus.Server.Core.Clients;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Realtime;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Tests.Clients;

/// <summary>
/// The update toast (<see cref="ClientUpdateGate.RequestModalsAsync"/>): sent to a connected player only, whose
/// connection stays open. Parity of the gate with the TS server is
/// tools/clients/gate_diff.mjs. Real Redis (database 15, OVS_TEST_REDIS).
/// </summary>
[Collection(RedisTestDatabase.Name)]
public sealed class ClientUpdateGateTests : IAsyncLifetime
{
    private static readonly string? s_redis = Environment.GetEnvironmentVariable("OVS_TEST_REDIS");
    private static string Id(int n) => $"0000000000000000001b{n:D4}";
    private static readonly string P1 = Id(1), P2 = Id(2);

    private ConnectionMultiplexer? _redis;
    private readonly ConcurrentQueue<(string Channel, string Message)> _published = new();

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
        foreach (string channel in new[] { ProfileNotifications.WsSendChannel })
        {
            await _redis.GetSubscriber().SubscribeAsync(RedisChannel.Literal(channel), (_, m) =>
            {
                if (m.ToString().Contains("0000000000000000001b", StringComparison.Ordinal))
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
        foreach (var key in server.Keys(15, "*0000000000000000001b*"))
        {
            await Db.KeyDeleteAsync(key);
        }

        foreach (var member in await Db.SortedSetRangeByScoreAsync(DelayedMessages.Key))
        {
            if (member.ToString().Contains("0000000000000000001b", StringComparison.Ordinal))
            {
                await Db.SortedSetRemoveAsync(DelayedMessages.Key, member);
            }
        }
    }

    private IDatabase Db => _redis!.GetDatabase();

    // The resolver serves ForRequestAsync only, which these tests do not call.
    private ClientUpdateGate Gate() => new(new ServiceCollection().AddSingleton<IConnectionMultiplexer>(_redis!).BuildServiceProvider(),
        null!, new TestOptions<ClientSettings>(new ClientSettings()));

    [SkippableFact]
    public async Task EachGameIsToastedAndKeepsItsConnection()
    {
        Skip.If(_redis is null, "OVS_TEST_REDIS not set");
        await Db.HashSetAsync(GatewayPresence.ConnectionKey(P1), "id", "c1");

        Assert.Equal([true, true], await Gate().RequestModalsAsync([P1, P2]));
        // Within the cooldown, nothing more.
        Assert.Equal([false, false], await Gate().RequestModalsAsync([P1, P2]));
        await Task.Delay(200);

        // One toast per player, through ws:send.
        Assert.All(_published, p => Assert.Equal(ProfileNotifications.WsSendChannel, p.Channel));
        var sends = _published.Select(p => (JsonObject)Js.Parse(p.Message)!).ToList();
        Assert.Equal([$"[\"{P1}\"]", $"[\"{P2}\"]"], sends.Select(s => s["playerIds"]!.ToJsonString()).Order());
        Assert.All(sends, send =>
        {
            Assert.Equal("""{"template_id":"ToastReceivedNotification","ToasterAccountID":"00000000000000000000a003","RewardsGranted":[]}""", Js.Stringify(send["message"]!["data"]));
            Assert.Equal("profile-notification", (string?)send["message"]!["cmd"]);
        });

        // Nothing closes the connection later.
        Assert.DoesNotContain(await Db.SortedSetRangeByScoreAsync(DelayedMessages.Key), e => e.ToString().Contains("0000000000000000001b", StringComparison.Ordinal));
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using OpenVersus.Server.Core.Hosting;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Tests.Hosting;

/// <summary>
/// Instances announce themselves in Redis and any one of them lists them all (ovsctl health). Real Redis, database 15:
/// OVS_TEST_REDIS, OVS_TEST_REDIS_USER, OVS_TEST_REDIS_PW, as OpsTests.
/// </summary>
[Collection(RedisTestDatabase.Name)]
public sealed class InstanceRegistryTests
{
    private const int TestRedisDb = 15;
    private static readonly string? s_redis = Environment.GetEnvironmentVariable("OVS_TEST_REDIS");

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task<(WebApplication App, int ControlPort)> StartAsync(string service)
    {
        string[] parts = s_redis!.Split(':');
        int controlPort = FreePort();
        var builder = OpenVersusHost.CreateBuilder(new ServiceDefinition(service, "TEST_PORT", 1, 1, ServiceStores.Redis),
        [
            $"--TEST_PORT={FreePort()}", $"--Control:Port={controlPort}", "--Control:Socket=off",
            $"--REDIS={parts[0]}", $"--REDIS_PORT={(parts.Length > 1 ? parts[1] : "6379")}",
            $"--REDIS_USERNAME={Environment.GetEnvironmentVariable("OVS_TEST_REDIS_USER") ?? ""}",
            $"--REDIS_PW={Environment.GetEnvironmentVariable("OVS_TEST_REDIS_PW") ?? ""}",
            $"--REDIS_DB={TestRedisDb}",
        ]);
        var app = builder.Build();
        app.UseOpenVersus();
        await app.StartAsync();
        return (app, controlPort);
    }

    private static async Task<List<InstanceReport>> MineAsync(IDatabase redis, params string[] services) =>
        (await InstanceRegistry.ReadAsync(redis, DateTimeOffset.UtcNow)).Instances.Where(i => services.Contains(i.Service)).ToList();

    // The registry is read until it shows what is expected: the first heartbeat is written just after the host starts.
    private static async Task<List<InstanceReport>> UntilAsync(IDatabase redis, Func<List<InstanceReport>, bool> done, params string[] services)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        List<InstanceReport> mine;
        do
        {
            mine = await MineAsync(redis, services);
            if (done(mine))
            {
                break;
            }

            await Task.Delay(100);
        }
        while (DateTime.UtcNow < deadline);
        return mine;
    }

    [SkippableFact]
    public async Task EveryInstanceIsListedByAnyOfThemAndAStoppedOneSaysSo()
    {
        Skip.If(string.IsNullOrEmpty(s_redis), "set OVS_TEST_REDIS to run");
        string a = $"regtest-a-{Guid.NewGuid():N}", b = $"regtest-b-{Guid.NewGuid():N}";
        var (first, firstControl) = await StartAsync(a);
        var (second, _) = await StartAsync(b);
        var redis = first.Services.GetRequiredService<IConnectionMultiplexer>().GetDatabase();
        try
        {
            var both = await UntilAsync(redis, m => m.Count == 2, a, b);
            Assert.Equal([a, b], both.Select(i => i.Service).Order());
            Assert.All(both, i => Assert.Equal("Ready", i.State));
            Assert.All(both, i => Assert.Equal("Healthy", i.Checks["redis"].Status));

            // Through the control API of either one.
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{firstControl}") };
            var view = await client.GetFromJsonAsync<JsonElement>("/control/cluster");
            var listed = view.GetProperty("instances").EnumerateArray().Select(i => i.GetProperty("service").GetString()).ToList();
            Assert.Contains(a, listed);
            Assert.Contains(b, listed);

            await second.StopAsync();
            var stopped = await UntilAsync(redis, m => m.Any(i => i.Service == b && i.State == "Stopped"), a, b);
            Assert.Equal("Stopped", stopped.Single(i => i.Service == b).State);
            Assert.Equal("Ready", stopped.Single(i => i.Service == a).State);
        }
        finally
        {
            await second.DisposeAsync();
            await first.StopAsync();
            foreach (var i in await MineAsync(redis, a, b))
            {
                await redis.KeyDeleteAsync(InstanceRegistry.Key(i.Instance));
                await redis.SortedSetRemoveAsync(InstanceRegistry.Index, $"{i.Service}/{i.Instance}");
            }

            await first.DisposeAsync();
        }
    }

    [SkippableFact]
    public async Task AnInstanceThatStopsHeartbeatingIsListedAsMissing()
    {
        Skip.If(string.IsNullOrEmpty(s_redis), "set OVS_TEST_REDIS to run");
        string service = $"regtest-gone-{Guid.NewGuid():N}";
        var (app, _) = await StartAsync(service);
        var redis = app.Services.GetRequiredService<IConnectionMultiplexer>().GetDatabase();
        try
        {
            var mine = await UntilAsync(redis, m => m.Count == 1, service);
            string id = Assert.Single(mine).Instance;
            // What a crash leaves: the index entry, and a key that expired. (The host keeps running; its next heartbeat is 5 s away.)
            await redis.KeyDeleteAsync(InstanceRegistry.Key(id));

            var gone = Assert.Single(await MineAsync(redis, service));
            Assert.Equal("Missing", gone.State);
            Assert.Equal(id, gone.Instance);
        }
        finally
        {
            await app.StopAsync();
            await redis.KeyDeleteAsync(InstanceRegistry.Key((await MineAsync(redis, service)).Select(i => i.Instance).FirstOrDefault() ?? "none"));
            await redis.SortedSetRemoveRangeByValueAsync(InstanceRegistry.Index, $"{service}/", $"{service}/￿");
            await app.DisposeAsync();
        }
    }
}

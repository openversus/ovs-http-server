using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OpenVersus.Server.Core.Control;
using OpenVersus.Server.Core.Hosting;
using Serilog.Core;
using Serilog.Events;

namespace OpenVersus.Server.Core.Tests;

/// <summary>
/// The control API on real listeners: it answers on its port and its socket and nowhere else, and changes made through
/// it reach the running service. With OVS_TEST_REDIS set (host:port, plus OVS_TEST_REDIS_USER / OVS_TEST_REDIS_PW),
/// also that a cluster change made on one replica reaches another through Redis.
/// </summary>
public sealed class ControlTests
{
    private sealed class Host : IAsyncDisposable
    {
        public required WebApplication App { get; init; }
        public required int PublicPort { get; init; }
        public required int ControlPort { get; init; }
        public required string Socket { get; init; }

        public HttpClient Public => new() { BaseAddress = new Uri($"http://127.0.0.1:{PublicPort}") };
        public HttpClient ControlPortClient => new() { BaseAddress = new Uri($"http://127.0.0.1:{ControlPort}") };

        public HttpClient ControlSocketClient => new(new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(Socket), ct);
                return new NetworkStream(socket, ownsSocket: true);
            },
        }) { BaseAddress = new Uri("http://localhost") };

        public async ValueTask DisposeAsync()
        {
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }

    private static int FreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    // Everything goes in as command-line arguments: those sit below the override layers, as the environment does in a
    // real deployment, so the layers win over them the same way.
    private static Task<Host> StartAsync(params string[] extra) => StartAsync(null, extra);

    private static Task<Host> StartAsync(Action<WebApplicationBuilder>? configure, params string[] extra) => StartAsync(ServiceStores.None, configure, extra);

    private static async Task<Host> StartAsync(ServiceStores needs, Action<WebApplicationBuilder>? configure, params string[] extra)
    {
        int publicPort = FreePort(), controlPort = FreePort();
        string socket = Path.Combine(Path.GetTempPath(), "ovs-tests", $"{Guid.NewGuid():N}.sock");
        // REDIS is empty unless a test sets it, whatever the environment says: arguments win over the environment.
        string[] args = [$"--TEST_PORT={publicPort}", $"--Control:Port={controlPort}", $"--Control:Socket={socket}", "--REDIS=", .. extra];
        var builder = OpenVersusHost.CreateBuilder(new ServiceDefinition("test", "TEST_PORT", DefaultPublicPort: 1, DefaultControlPort: 1, needs), args);
        configure?.Invoke(builder);
        var app = builder.Build();
        app.UseOpenVersus();
        app.MapGet("/hello", () => "hello");
        await app.StartAsync();
        return new Host { App = app, PublicPort = publicPort, ControlPort = controlPort, Socket = socket };
    }

    [SkippableFact]
    public async Task TheControlApiAnswersOnItsPortAndSocketAndNotThePublicPort()
    {
        Skip.IfNot(Socket.OSSupportsUnixDomainSockets, "no Unix sockets here");
        await using var host = await StartAsync();

        Assert.Equal("hello", await host.Public.GetStringAsync("/hello"));
        Assert.Equal(HttpStatusCode.NotFound, (await host.Public.GetAsync("/control/status")).StatusCode);

        foreach (var client in new[] { host.ControlPortClient, host.ControlSocketClient })
        {
            var status = await client.GetFromJsonAsync<JsonElement>("/control/status");
            Assert.Equal("test", status.GetProperty("service").GetString());
            Assert.False(status.GetProperty("sharedSettings").GetBoolean());
        }
    }

    [Fact]
    public async Task ASettingChangedThroughTheControlApiReachesTheService()
    {
        await using var host = await StartAsync();
        var client = host.ControlPortClient;
        var levels = host.App.Services.GetRequiredService<LoggingLevelSwitch>();
        Assert.Equal(LogEventLevel.Information, levels.MinimumLevel);

        var put = await client.PutAsync("/control/settings/Log:Level?scope=instance", new StringContent("Debug"));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(LogEventLevel.Debug, levels.MinimumLevel);
        var view = await client.GetFromJsonAsync<JsonElement>("/control/settings/Log:Level");
        Assert.Equal("Debug", view.GetProperty("instance").GetString());
        Assert.Equal("Debug", view.GetProperty("value").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsync("/control/settings/Log:Level?scope=instance", new StringContent("Loud"))).StatusCode);
        Assert.Equal(LogEventLevel.Debug, levels.MinimumLevel);

        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync("/control/settings/Log:Level?scope=instance")).StatusCode);
        Assert.Equal(LogEventLevel.Information, levels.MinimumLevel);
    }

    [Fact]
    public async Task HealthAnswersOnEveryListener()
    {
        await using var host = await StartAsync();
        foreach (var client in new[] { host.Public, host.ControlPortClient })
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
        }
    }

    [Fact]
    public async Task WithRedisUnreachableTheReplicaIsAliveButNotReady()
    {
        // Nothing listens on port 1: the multiplexer keeps trying in the background, and the replica stays up.
        await using var host = await StartAsync("--REDIS=127.0.0.1", "--REDIS_PORT=1");
        Assert.Equal(HttpStatusCode.OK, (await host.Public.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await host.Public.GetAsync("/health/ready")).StatusCode);
    }

    [Fact]
    public async Task AServiceThatNeedsAStoreItHasNoSettingForIsAliveButNotReadyAndSaysWhy()
    {
        await using var host = await StartAsync(ServiceStores.Redis | ServiceStores.Mongo, null, "--MONGODB_URI=");
        Assert.Equal(HttpStatusCode.OK, (await host.Public.GetAsync("/health/live")).StatusCode);
        var ready = await host.Public.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        var checks = (await ready.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("checks");
        Assert.Contains("REDIS is not set", checks.GetProperty("redis").GetProperty("description").GetString());
        Assert.Contains("MONGODB_URI is not set", checks.GetProperty("mongo").GetProperty("description").GetString());
    }

    [Fact]
    public async Task WithMongoUnreachableTheReplicaIsNotReadyAndSaysSoQuickly()
    {
        // Nothing listens on port 1. The driver would look for a server for 30 s; the check gives up after 2.
        await using var host = await StartAsync(ServiceStores.Mongo, null, "--MONGODB_URI=mongodb://127.0.0.1:1/ovs_unreachable");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var ready = await host.Public.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
        Assert.Equal("Unhealthy", (await ready.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("checks").GetProperty("mongo").GetProperty("status").GetString());
    }

    [SkippableFact]
    public async Task WithMongoReachableTheReplicaIsReady()
    {
        string? mongo = Environment.GetEnvironmentVariable("OVS_TEST_MONGO");
        Skip.If(string.IsNullOrEmpty(mongo), "set OVS_TEST_MONGO to run");
        await using var host = await StartAsync(ServiceStores.Mongo, null, $"--MONGODB_URI={mongo}");
        Assert.Equal(HttpStatusCode.OK, (await host.Public.GetAsync("/health/ready")).StatusCode);
    }

    private sealed class AllowEveryone : IControlAccessPolicy
    {
        public bool Allows(HttpContext context) => true;
    }

    [Fact]
    public async Task TheAccessPolicyIsWhatDecidesWhoReachesTheControlApi()
    {
        await using var host = await StartAsync(b => b.Services.AddSingleton<IControlAccessPolicy, AllowEveryone>());
        Assert.Equal(HttpStatusCode.OK, (await host.Public.GetAsync("/control/status")).StatusCode);
    }

    [Fact]
    public async Task AnOverrideWinsOverWhatTheServiceWasStartedWith()
    {
        // The command line is the highest of the start-up sources; an override still has to beat it.
        await using var host = await StartAsync("--Log:Level=Warning");
        var levels = host.App.Services.GetRequiredService<LoggingLevelSwitch>();
        Assert.Equal(LogEventLevel.Warning, levels.MinimumLevel);

        Assert.Equal(HttpStatusCode.OK, (await host.ControlPortClient.PutAsync("/control/settings/Log:Level?scope=instance", new StringContent("Error"))).StatusCode);
        Assert.Equal(LogEventLevel.Error, levels.MinimumLevel);
    }

    [Fact]
    public async Task WithoutRedisClusterChangesAreRefusedBothWays()
    {
        await using var host = await StartAsync();
        var client = host.ControlPortClient;
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsync("/control/settings/Log:Level?scope=cluster", new StringContent("Debug"))).StatusCode);
        var delete = await client.DeleteAsync("/control/settings/Log:Level?scope=cluster");
        Assert.Equal(HttpStatusCode.BadRequest, delete.StatusCode);
        Assert.Contains("Redis", await delete.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ASettingThatOnlyTakesEffectAtStartupIsRefused()
    {
        await using var host = await StartAsync();
        var put = await host.ControlPortClient.PutAsync("/control/settings/Control:Port?scope=instance", new StringContent("1234"));
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Contains("startup", await put.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task EverySettingIsListedWithItsDescription()
    {
        await using var host = await StartAsync();
        var list = await host.ControlPortClient.GetFromJsonAsync<JsonElement[]>("/control/settings");
        Assert.NotNull(list);
        var keys = list!.Select(s => s.GetProperty("key").GetString()).ToHashSet();
        Assert.Contains("Log:Level", keys);
        Assert.Contains("Control:Port", keys);
        Assert.All(list!, s => Assert.False(string.IsNullOrEmpty(s.GetProperty("description").GetString())));
    }

    [SkippableFact]
    public async Task AClusterChangeOnOneReplicaReachesAnother()
    {
        string? redis = Environment.GetEnvironmentVariable("OVS_TEST_REDIS");
        Skip.If(string.IsNullOrEmpty(redis), "set OVS_TEST_REDIS=host:port (and OVS_TEST_REDIS_USER / OVS_TEST_REDIS_PW) to run");
        string[] parts = redis!.Split(':');
        string[] redisArgs =
        [
            $"--REDIS={parts[0]}", $"--REDIS_PORT={(parts.Length > 1 ? parts[1] : "6379")}",
            $"--REDIS_USERNAME={Environment.GetEnvironmentVariable("OVS_TEST_REDIS_USER") ?? ""}",
            $"--REDIS_PW={Environment.GetEnvironmentVariable("OVS_TEST_REDIS_PW") ?? ""}",
            // A database of its own: the default (0) is a live one's, and this changes a cluster setting there. Not 15,
            // which OpsTests empties while other test classes run.
            "--REDIS_DB=12",
        ];
        await using var a = await StartAsync(redisArgs);
        await using var b = await StartAsync(redisArgs);
        var levelsB = b.App.Services.GetRequiredService<LoggingLevelSwitch>();
        try
        {
            var put = await a.ControlPortClient.PutAsync("/control/settings/Log:Level?scope=cluster", new StringContent("Warning"));
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
            Assert.True(await Eventually(() => levelsB.MinimumLevel == LogEventLevel.Warning), "replica b never saw the change");

            Assert.Equal(HttpStatusCode.OK, (await a.ControlPortClient.DeleteAsync("/control/settings/Log:Level?scope=cluster")).StatusCode);
            Assert.True(await Eventually(() => levelsB.MinimumLevel == LogEventLevel.Information), "replica b never saw the removal");
        }
        finally
        {
            await a.ControlPortClient.DeleteAsync("/control/settings/Log:Level?scope=cluster");
        }
    }

    private static async Task<bool> Eventually(Func<bool> condition)
    {
        for (int i = 0; i < 50; i++)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(100);
        }

        return condition();
    }
}

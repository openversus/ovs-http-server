using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using OpenVersus.Server.Core.Control;
using OpenVersus.Server.Core.Logging;
using OpenVersus.Server.Core.Settings;
using MongoDB.Driver;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Hosting;

/// <summary>
/// One OpenVersus service (one container, any number of replicas): its name, the configuration key its public port is
/// read from (the TS server's names, so the containers' .env files carry over; null for a service with no public
/// listener, like the matchmaking worker), and its defaults.
/// </summary>
public sealed record ServiceDefinition(string Name, string? PublicPortKey, int DefaultPublicPort, int DefaultControlPort);

/// <summary>
/// The services, in one place: the hosts start from these and the CLI's --service picks from them. The control ports
/// differ so that one of each can run on one machine without colliding.
/// </summary>
public static class KnownServices
{
    public static readonly ServiceDefinition Http = new("http", "HTTP_PORT", DefaultPublicPort: 8000, DefaultControlPort: 17801);
    public static readonly ServiceDefinition Realtime = new("ws", "WEBSOCKET_PORT", DefaultPublicPort: 3000, DefaultControlPort: 17802);
    public static readonly ServiceDefinition Matchmaking = new("matchmaking", PublicPortKey: null, DefaultPublicPort: 0, DefaultControlPort: 17803);

    /// <summary>The migration's reverse proxy: ported routes to the C# services, the rest to the TS server.</summary>
    public static readonly ServiceDefinition Proxy = new("proxy", "PROXY_PORT", DefaultPublicPort: 8080, DefaultControlPort: 17804);

    public static IReadOnlyList<ServiceDefinition> All { get; } = [Http, Realtime, Matchmaking, Proxy];

    public static ServiceDefinition? Find(string name) => All.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>This process: a replica of a service. The id tells replicas apart in logs and in the control API.</summary>
public sealed class ServiceInstance
{
    public string Id { get; } = $"{Environment.MachineName}:{Environment.ProcessId}";

    public DateTimeOffset Started { get; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// What every service host starts from: configuration, logging, settings, Redis when it is configured, the public listener
/// and the control listeners. A service adds its own settings with <see cref="SettingsServiceCollectionExtensions.AddSetting{T}"/>
/// and its own services, then calls <see cref="UseOpenVersus"/> after building.
/// <para>
/// Configuration, lowest to highest: appsettings, the environment and the command line (all three from
/// <c>WebApplication.CreateBuilder</c>), the TS server's variable names for keys not set otherwise (<see cref="TsEnvironment"/>),
/// then the cluster layer (Redis) and the instance layer. The two override layers
/// are added after the builder has added the others, which is what makes a change through the control API win over
/// everything the service was started with; a source added after them would win over them instead.
/// </para>
/// </summary>
public static class OpenVersusHost
{
    public static WebApplicationBuilder CreateBuilder(ServiceDefinition service, string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var layers = new SettingsLayers(new OverrideLayer("cluster"), new OverrideLayer("instance"));
        IConfigurationBuilder config = builder.Configuration;
        TsEnvironment.AddAliases(config, builder.Configuration);
        config.Add(layers.Cluster);
        config.Add(layers.Instance);

        builder.Services.AddSingleton(service);
        builder.Services.AddSingleton(new ServiceInstance());
        builder.Services.AddSingleton(layers);
        builder.Services.AddSingleton(new SettingsCatalog());
        builder.AddOpenVersusLogging();
        builder.AddSetting<ControlSettings>("Control");
        builder.Services.AddSingleton<RuntimeSettings>();
        builder.Services.AddSingleton<IControlService, ControlService>();
        builder.Services.AddSingleton<IControlAccessPolicy, LocalControlListenersOnly>();
        builder.Services.AddHostedService<ClusterSettingsSync>();
        AddRedis(builder, service);
        AddMongo(builder);
        builder.Services.AddSingleton<Ops.IOpsService, Ops.OpsService>();

        var bound = new ControlListeners.Bound();
        builder.Services.AddSingleton(bound);
        builder.WebHost.ConfigureKestrel((context, kestrel) =>
        {
            if (service.PublicPortKey is not null)
            {
                kestrel.ListenAnyIP(context.Configuration.GetValue<int?>(service.PublicPortKey) ?? service.DefaultPublicPort);
            }

            ControlListeners.Configure(kestrel, context.Configuration, service, bound);
        });
        return builder;
    }

    /// <summary>
    /// Wires what needs the built app: the log level follows its setting, the control API is mapped, and so are the
    /// health endpoints a load balancer or orchestrator uses to take a replica in and out of rotation:
    /// <c>/health/live</c> (the process answers) and <c>/health/ready</c> (it can do its job: Redis is connected, when
    /// Redis is configured). They answer on every listener, public and control.
    /// </summary>
    public static WebApplication UseOpenVersus(this WebApplication app)
    {
        OpenVersusLogging.FollowLevelSetting(app.Services);
        app.LogFrozenAccountData();
        app.MapOpenVersusControl();
        app.Logger.LogWarning("MIGRATION BRIDGE: the control API's player disconnect asks the TS websocket to close the connection (ws:disconnect); see dotnet/docs/MIGRATION-BRIDGES.md (5)");
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
        return app;
    }

    // Mongo from the TS server's MONGODB_URI; the database is the one the URI names. Without it, nothing that needs
    // Mongo (player operations) is available.
    private static void AddMongo(WebApplicationBuilder builder)
    {
        string? uri = builder.Configuration["MONGODB_URI"];
        if (string.IsNullOrWhiteSpace(uri))
        {
            return;
        }

        var url = MongoUrl.Create(uri);
        if (string.IsNullOrEmpty(url.DatabaseName))
        {
            throw new InvalidOperationException("MONGODB_URI names no database (mongodb://host/<database>)");
        }

        builder.Services.AddSingleton<IMongoClient>(_ => new MongoClient(url));
        builder.Services.AddSingleton(sp => sp.GetRequiredService<IMongoClient>().GetDatabase(url.DatabaseName));
    }

    // Redis from the TS server's variables (REDIS, REDIS_PORT, REDIS_USERNAME, REDIS_PW), plus REDIS_DB, the database
    // number (0 unless set, like the TS server; the tests use another so they never touch a live queue). Without
    // REDIS, settings are this instance's only.
    private static void AddRedis(WebApplicationBuilder builder, ServiceDefinition service)
    {
        string? host = builder.Configuration["REDIS"];
        if (string.IsNullOrWhiteSpace(host))
        {
            builder.Services.AddSingleton<IClusterSettingsStore, LocalOnlySettingsStore>();
            builder.Services.AddHealthChecks();
            return;
        }

        var options = new ConfigurationOptions
        {
            EndPoints = { { host, builder.Configuration.GetValue("REDIS_PORT", 6379) } },
            User = builder.Configuration["REDIS_USERNAME"],
            Password = builder.Configuration["REDIS_PW"],
            ClientName = $"ovs-{service.Name}",
            DefaultDatabase = builder.Configuration.GetValue("REDIS_DB", 0),
            // Keep retrying in the background instead of failing startup when Redis is late.
            AbortOnConnectFail = false,
        };
        builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(options));
        builder.Services.AddSingleton<IClusterSettingsStore, RedisSettingsStore>();
        builder.Services.AddHealthChecks().AddCheck<RedisConnected>("redis", tags: ["ready"]);
    }

    // Ready only while the Redis connection is up: without it this replica has no shared settings and (once ported)
    // no shared state, so it should be out of rotation until it reconnects, which the multiplexer keeps trying to do.
    private sealed class RedisConnected : IHealthCheck
    {
        private readonly IConnectionMultiplexer _redis;

        public RedisConnected(IConnectionMultiplexer redis)
        {
            _redis = redis;
        }

        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(_redis.IsConnected ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("not connected to Redis"));
    }
}

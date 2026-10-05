using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
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
/// listener, like the matchmaking worker), its defaults, and the stores it cannot do its job without.
/// </summary>
public sealed record ServiceDefinition(string Name, string? PublicPortKey, int DefaultPublicPort, int DefaultControlPort,
    ServiceStores Needs = ServiceStores.None);

/// <summary>
/// Stores a service needs: it is not ready (<c>/health/ready</c> answers 503) while one is not configured or cannot be
/// reached, so a load balancer or orchestrator keeps it out of rotation instead of sending it work it cannot do.
/// <para>
/// Stores only, never another OpenVersus service: readiness that waits on another service can wait forever (A on B, B
/// on A), and turns one service's outage into every service's. Liveness (<c>/health/live</c>) depends on nothing. The
/// host tests pin each executable's readiness checks to these stores.
/// </para>
/// </summary>
[Flags]
public enum ServiceStores
{
    None = 0,
    Redis = 1,
    Mongo = 2,
}

/// <summary>
/// The services, in one place: the hosts start from these and the CLI's --service picks from them. The control ports
/// differ so that one of each can run on one machine without colliding.
/// </summary>
public static class KnownServices
{
    public static readonly ServiceDefinition Http = new("http", "HTTP_PORT", DefaultPublicPort: 8000, DefaultControlPort: 17801,
        ServiceStores.Redis | ServiceStores.Mongo);
    public static readonly ServiceDefinition Realtime = new("ws", "WEBSOCKET_PORT", DefaultPublicPort: 3000, DefaultControlPort: 17802,
        ServiceStores.Redis);
    public static readonly ServiceDefinition Matchmaking = new("matchmaking", PublicPortKey: null, DefaultPublicPort: 0, DefaultControlPort: 17803,
        ServiceStores.Redis);

    /// <summary>
    /// A match from its start to what it changes once played (MATCHFLOW_PORT): check-ins, concedes and match config, the
    /// rollback server's callbacks, and the results (missions, match XP, rift progress).
    /// </summary>
    public static readonly ServiceDefinition MatchFlow = new("matchflow", "MATCHFLOW_PORT", DefaultPublicPort: 8005, DefaultControlPort: 17805,
        ServiceStores.Redis | ServiceStores.Mongo);

    /// <summary>The game's login and sessions (ACCESS_PORT): /access, /sessions/*, and the bans they check.</summary>
    public static readonly ServiceDefinition Access = new("access", "ACCESS_PORT", DefaultPublicPort: 8001, DefaultControlPort: 17806,
        ServiceStores.Redis | ServiceStores.Mongo);

    /// <summary>Friends, relationships and blocks (SOCIAL_PORT), the game's and the OpenVersus client's.</summary>
    public static readonly ServiceDefinition Social = new("social", "SOCIAL_PORT", DefaultPublicPort: 8002, DefaultControlPort: 17807,
        ServiceStores.Redis | ServiceStores.Mongo);

    /// <summary>
    /// Parties, party and custom lobbies, the rift lobby, queueing and the game's /matches routes (LOBBIES_PORT): what
    /// happens before a match starts.
    /// </summary>
    public static readonly ServiceDefinition Lobbies = new("lobbies", "LOBBIES_PORT", DefaultPublicPort: 8003, DefaultControlPort: 17808,
        ServiceStores.Redis | ServiceStores.Mongo);

    /// <summary>The website and the OpenVersus client's API (WEB_PORT), and the admin pages until the control plane takes them.</summary>
    public static readonly ServiceDefinition Web = new("web", "WEB_PORT", DefaultPublicPort: 8004, DefaultControlPort: 17809,
        ServiceStores.Redis | ServiceStores.Mongo);

    /// <summary>The migration's reverse proxy: ported routes to the C# services, the rest to the TS server.</summary>
    public static readonly ServiceDefinition Proxy = new("proxy", "PROXY_PORT", DefaultPublicPort: 8080, DefaultControlPort: 17804);

    public static IReadOnlyList<ServiceDefinition> All { get; } = [Http, Access, Social, Lobbies, Web, Realtime, Matchmaking, MatchFlow, Proxy];

    public static ServiceDefinition? Find(string name) => All.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>This process: a replica of a service. The id tells replicas apart in logs and in the control API.</summary>
public sealed class ServiceInstance
{
    // The machine and process, and a random part: in containers the process is always 1, and containers on the host's
    // network share its machine name.
    public string Id { get; } = $"{Environment.MachineName}:{Environment.ProcessId}:{Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(3))}";

    /// <summary>The build: the version, and the commit it was built from after the +.</summary>
    public string? Version { get; } = System.Reflection.Assembly.GetEntryAssembly()?
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion;

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
        builder.Services.AddHostedService<InstanceHeartbeat>();
        var health = builder.Services.AddHealthChecks();
        AddRedis(builder, service, health);
        AddMongo(builder, service, health);
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
    /// <c>/health/live</c> (the process answers) and <c>/health/ready</c> (it can do its job: every store it needs,
    /// <see cref="ServiceDefinition.Needs"/>, is configured and reachable; and Redis is connected wherever it is configured).
    /// They answer on every listener, public and control; <c>/health/ready</c> says which check failed and why (JSON).
    /// </summary>
    public static WebApplication UseOpenVersus(this WebApplication app)
    {
        OpenVersusLogging.FollowLevelSetting(app.Services);
        app.LogFrozenAccountData();
        app.MapOpenVersusControl();
        app.Logger.LogWarning("MIGRATION BRIDGE: the control API's player disconnect asks the TS websocket to close the connection (ws:disconnect); see dotnet/docs/MIGRATION-BRIDGES.md (5)");
        Matches.MatchLauncherHosting.WarnP2PBridge(app);
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready"), ResponseWriter = WriteReadyAsync });
        var service = app.Services.GetRequiredService<ServiceDefinition>();
        foreach (var (store, key) in MissingStores(app.Configuration, service))
        {
            app.Logger.LogWarning("The {Service} service needs {Store} and {Key} is not set: it will not be ready (/health/ready) until it is", service.Name, store, key);
        }

        return app;
    }

    /// <summary>The readiness answer: the overall status and each check's, with the reason a check failed.</summary>
    private static Task WriteReadyAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        var checks = report.Entries.ToDictionary(e => e.Key, e => new { status = e.Value.Status.ToString(), description = e.Value.Description });
        return context.Response.WriteAsJsonAsync(new { status = report.Status.ToString(), checks });
    }

    // The stores this service needs and lacks the setting for: (store, setting).
    private static IEnumerable<(string Store, string Key)> MissingStores(IConfiguration configuration, ServiceDefinition service)
    {
        if (service.Needs.HasFlag(ServiceStores.Redis) && string.IsNullOrWhiteSpace(configuration["REDIS"]))
        {
            yield return ("Redis", "REDIS");
        }

        if (service.Needs.HasFlag(ServiceStores.Mongo) && string.IsNullOrWhiteSpace(configuration["MONGODB_URI"]))
        {
            yield return ("Mongo", "MONGODB_URI");
        }
    }

    // A store the service needs and has no setting for: never ready.
    private static void AddMissing(IHealthChecksBuilder health, string name, string store, string key) =>
        health.AddCheck(name, () => HealthCheckResult.Unhealthy($"this service needs {store} and {key} is not set"), tags: ["ready"]);

    // Mongo from the TS server's MONGODB_URI; the database is the one the URI names. Without it, nothing that needs
    // Mongo (player operations) is available.
    private static void AddMongo(WebApplicationBuilder builder, ServiceDefinition service, IHealthChecksBuilder health)
    {
        string? uri = builder.Configuration["MONGODB_URI"];
        if (string.IsNullOrWhiteSpace(uri))
        {
            if (service.Needs.HasFlag(ServiceStores.Mongo))
            {
                AddMissing(health, "mongo", "Mongo", "MONGODB_URI");
            }

            return;
        }

        var url = MongoUrl.Create(uri);
        if (string.IsNullOrEmpty(url.DatabaseName))
        {
            throw new InvalidOperationException("MONGODB_URI names no database (mongodb://host/<database>)");
        }

        builder.Services.AddSingleton<IMongoClient>(_ => new MongoClient(url));
        builder.Services.AddSingleton(sp => sp.GetRequiredService<IMongoClient>().GetDatabase(url.DatabaseName));
        if (service.Needs.HasFlag(ServiceStores.Mongo))
        {
            // Bounded: the driver waits up to 30 s to find a server, and a probe must answer sooner than its prober gives up.
            health.AddCheck<MongoReachable>("mongo", tags: ["ready"], timeout: TimeSpan.FromSeconds(2));
        }
    }

    // Redis from the TS server's variables (REDIS, REDIS_PORT, REDIS_USERNAME, REDIS_PW), plus REDIS_DB, the database
    // number (0 unless set, like the TS server; the tests use another so they never touch a live queue). Without
    // REDIS, settings are this instance's only.
    private static void AddRedis(WebApplicationBuilder builder, ServiceDefinition service, IHealthChecksBuilder health)
    {
        string? host = builder.Configuration["REDIS"];
        if (string.IsNullOrWhiteSpace(host))
        {
            builder.Services.AddSingleton<IClusterSettingsStore, LocalOnlySettingsStore>();
            if (service.Needs.HasFlag(ServiceStores.Redis))
            {
                AddMissing(health, "redis", "Redis", "REDIS");
            }

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
        health.AddCheck<RedisConnected>("redis", tags: ["ready"]);
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

    // Ready only while Mongo answers a ping (within the check's timeout).
    private sealed class MongoReachable(IMongoDatabase mongo) : IHealthCheck
    {
        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            try
            {
                await mongo.RunCommandAsync<MongoDB.Bson.BsonDocument>(new MongoDB.Bson.BsonDocument("ping", 1), cancellationToken: cancellationToken);
                return HealthCheckResult.Healthy();
            }
            catch (Exception e) when (e is MongoException or TimeoutException)
            {
                return HealthCheckResult.Unhealthy($"cannot reach Mongo: {e.Message}");
            }
        }
    }
}

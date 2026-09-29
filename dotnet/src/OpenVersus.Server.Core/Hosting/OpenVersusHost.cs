using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenVersus.Server.Core.Control;
using OpenVersus.Server.Core.Logging;
using OpenVersus.Server.Core.Settings;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Hosting;

/// <summary>
/// One OpenVersus service (one container): its name, the configuration key its public port is read from (the TS
/// server's names, so the containers' .env files carry over), and its defaults.
/// </summary>
public sealed record ServiceDefinition(string Name, string PublicPortKey, int DefaultPublicPort, int DefaultControlPort);

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
/// <c>WebApplication.CreateBuilder</c>), then the cluster layer (Redis) and the instance layer. The two override layers
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
        config.Add(layers.Cluster);
        config.Add(layers.Instance);

        builder.Services.AddSingleton(service);
        builder.Services.AddSingleton(new ServiceInstance());
        builder.Services.AddSingleton(layers);
        builder.Services.AddSingleton(new SettingsCatalog());
        builder.AddOpenVersusLogging();
        builder.AddSetting<ControlSettings>("Control");
        builder.Services.AddSingleton<RuntimeSettings>();
        builder.Services.AddHostedService<ClusterSettingsSync>();
        AddRedis(builder, service);

        var bound = new ControlListeners.Bound();
        builder.Services.AddSingleton(bound);
        builder.WebHost.ConfigureKestrel((context, kestrel) =>
        {
            kestrel.ListenAnyIP(context.Configuration.GetValue<int?>(service.PublicPortKey) ?? service.DefaultPublicPort);
            ControlListeners.Configure(kestrel, context.Configuration, service, bound);
        });
        return builder;
    }

    /// <summary>Wires what needs the built app: the log level follows its setting, and the control API is mapped.</summary>
    public static WebApplication UseOpenVersus(this WebApplication app)
    {
        OpenVersusLogging.FollowLevelSetting(app.Services);
        app.MapOpenVersusControl();
        return app;
    }

    // Redis from the TS server's variables (REDIS, REDIS_PORT, REDIS_USERNAME, REDIS_PW). Without REDIS, settings are
    // this instance's only.
    private static void AddRedis(WebApplicationBuilder builder, ServiceDefinition service)
    {
        string? host = builder.Configuration["REDIS"];
        if (string.IsNullOrWhiteSpace(host))
        {
            builder.Services.AddSingleton<IClusterSettingsStore, LocalOnlySettingsStore>();
            return;
        }

        var options = new ConfigurationOptions
        {
            EndPoints = { { host, builder.Configuration.GetValue("REDIS_PORT", 6379) } },
            User = builder.Configuration["REDIS_USERNAME"],
            Password = builder.Configuration["REDIS_PW"],
            ClientName = $"ovs-{service.Name}",
            // Keep retrying in the background instead of failing startup when Redis is late.
            AbortOnConnectFail = false,
        };
        builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(options));
        builder.Services.AddSingleton<IClusterSettingsStore, RedisSettingsStore>();
    }
}

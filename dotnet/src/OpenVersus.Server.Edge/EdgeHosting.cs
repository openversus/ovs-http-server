using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using OpenVersus.Server.Core.Control;
using OpenVersus.Server.Core.Realtime;
using OpenVersus.Server.Core.Settings;

namespace OpenVersus.Server.Edge;

public static class EdgeHosting
{
    public static WebApplicationBuilder AddEdge(this WebApplicationBuilder builder)
    {
        builder.AddSetting<EdgeSettings>("Edge");
        // The link's secret and the nodes' grace, shared with the gateway nodes.
        builder.AddSetting<GatewaySettings>("Gateway");
        builder.Services.TryAddTimeProvider();
        builder.Services.AddSingleton<EdgeNodes>();
        builder.Services.AddSingleton<EdgeGames>();

        // A stopping edge keeps its games until they leave (Edge:DrainTimeoutMs, 0: no limit): the server's stop waits for
        // its open requests, and every game is one.
        int drainMs = builder.Configuration.GetValue<int?>("Edge:DrainTimeoutMs") ?? 0;
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = drainMs > 0 ? TimeSpan.FromMilliseconds(drainMs) : Timeout.InfiniteTimeSpan);
        builder.Services.AddHealthChecks()
            .AddCheck<DrainingCheck>("draining", tags: ["ready"])
            .AddCheck<EdgeSecretCheck>("edge-secret", tags: ["ready"]);
        return builder;
    }

    /// <summary>
    /// Every websocket upgrade on the public listener, whatever its path, is a game (as on a gateway node); any other request
    /// there but /health/* is answered as a node answers it. Websocket keep-alive frames toward the game are off, as on a
    /// node: the game is pinged with its own message, by the node (or by the edge while the game has none).
    /// </summary>
    public static WebApplication UseEdge(this WebApplication app)
    {
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.Zero });
        var games = app.Services.GetRequiredService<EdgeGames>();
        var settings = app.Services.GetRequiredService<IOptionsMonitor<EdgeSettings>>().CurrentValue;
        var gateway = app.Services.GetRequiredService<IOptionsMonitor<GatewaySettings>>().CurrentValue;
        if (settings.GiveUpMs >= gateway.EdgeDetachGraceMs || settings.GiveUpMs >= PlayerMessages.ReplayWindow.TotalMilliseconds)
        {
            app.Logger.LogError("Edge:GiveUpMs ({GiveUp} ms) must be below Gateway:EdgeDetachGraceMs ({Grace} ms) and the replay window ({Window} s): a game moved later may miss messages, or find its node gave it up",
                settings.GiveUpMs, gateway.EdgeDetachGraceMs, PlayerMessages.ReplayWindow.TotalSeconds);
        }

        app.Use(async (context, next) =>
        {
            if (ControlListeners.IsControl(context) || context.Request.Path.StartsWithSegments("/health"))
            {
                await next(context);
            }
            else if (context.WebSockets.IsWebSocketRequest)
            {
                await games.HandleAsync(context);
            }
            else
            {
                context.Response.ContentType = "text/plain";
                await context.Response.WriteAsync("HTTP server is running\n");
            }
        });
        return app;
    }

    private static void TryAddTimeProvider(this IServiceCollection services)
    {
        if (!services.Any(d => d.ServiceType == typeof(TimeProvider)))
        {
            services.AddSingleton(TimeProvider.System);
        }
    }
}

/// <summary>A stopping edge is not ready: the proxy sends it no new games (those it holds stay until they leave).</summary>
internal sealed class DrainingCheck(IHostApplicationLifetime lifetime) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(lifetime.ApplicationStopping.IsCancellationRequested
            ? HealthCheckResult.Unhealthy("stopping: keeping its games until they leave, taking no new ones")
            : HealthCheckResult.Healthy());
}

/// <summary>Without Gateway:EdgeSecret no node takes a link: not ready.</summary>
internal sealed class EdgeSecretCheck(IOptionsMonitor<GatewaySettings> gateway) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(string.IsNullOrEmpty(gateway.CurrentValue.EdgeSecret)
            ? HealthCheckResult.Unhealthy("Gateway:EdgeSecret is not set: no gateway node would take a game from this edge")
            : HealthCheckResult.Healthy());
}

using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using OpenVersus.Server.Core.Hosting;
using OpenVersus.Server.Core.Settings;

namespace OpenVersus.Server.Core.Control;

/// <summary>Where the control API listens. Only read at startup: the listeners are bound once.</summary>
[RestartRequired]
public sealed class ControlSettings
{
    [Description("The control API's Unix socket. Empty: <temp>/openversus/<service>.sock. \"off\": no socket.")]
    public string Socket { get; set; } = "";

    [Description("The control API's port on 127.0.0.1; 0 for none. Empty: the service's default.")]
    [Range(0, 65535)]
    public int? Port { get; set; }

    internal string SocketPath(ServiceDefinition service) => Socket switch
    {
        "off" => "",
        "" => Path.Combine(Path.GetTempPath(), "openversus", $"{service.Name}.sock"),
        _ => Socket,
    };
}

/// <summary>
/// The control API's two listeners, a Unix socket and a port on 127.0.0.1, both only reachable from the same machine
/// (or container). Both are there because Unix sockets are unreliable on Windows. Connections that arrive on them
/// are marked, and the control endpoints answer nothing else: on the public listener they are 404.
/// </summary>
public static class ControlListeners
{
    private const string Mark = "ovs.control";

    /// <summary>The control listeners' actual addresses, known once the server has bound them.</summary>
    public sealed class Bound
    {
        internal ListenOptions? PortOptions { get; set; }

        public string? SocketPath { get; internal set; }

        public int? Port => PortOptions?.IPEndPoint?.Port;
    }

    internal static void Configure(KestrelServerOptions kestrel, IConfiguration config, ServiceDefinition service, Bound bound)
    {
        var settings = config.GetSection("Control").Get<ControlSettings>() ?? new ControlSettings();
        int port = settings.Port ?? service.DefaultControlPort;
        if (port > 0)
        {
            kestrel.Listen(IPAddress.Loopback, port, o =>
            {
                o.Use(MarkConnection);
                bound.PortOptions = o;
            });
        }

        string socket = settings.SocketPath(service);
        if (socket != "" && System.Net.Sockets.Socket.OSSupportsUnixDomainSockets)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(socket)!);
            // A socket file left by a process that did not shut down cleanly would stop the bind.
            File.Delete(socket);
            kestrel.ListenUnixSocket(socket, o => o.Use(MarkConnection));
            bound.SocketPath = socket;
        }
    }

    private static Microsoft.AspNetCore.Connections.ConnectionDelegate MarkConnection(Microsoft.AspNetCore.Connections.ConnectionDelegate next) => connection =>
    {
        connection.Items[Mark] = true;
        return next(connection);
    };

    /// <summary>True when the request came in on a control listener.</summary>
    public static bool IsControl(HttpContext context) =>
        context.Features.Get<IConnectionItemsFeature>()?.Items.ContainsKey(Mark) == true;
}

/// <summary>The control API the CLI talks to: status, and reading and changing settings while the service runs.</summary>
public static class ControlApi
{
    public static IEndpointRouteBuilder MapOpenVersusControl(this IEndpointRouteBuilder routes)
    {
        var control = routes.MapGroup("/control").AddEndpointFilter(async (context, next) =>
            ControlListeners.IsControl(context.HttpContext) ? await next(context) : Results.NotFound());

        control.MapGet("/status", (ServiceDefinition service, RuntimeSettings settings, ServiceInstance instance) => Results.Ok(new
        {
            service = service.Name,
            instance = instance.Id,
            version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            started = instance.Started,
            uptimeSeconds = (long)(DateTimeOffset.UtcNow - instance.Started).TotalSeconds,
            sharedSettings = settings.ClusterShared,
        }));

        control.MapGet("/settings", (RuntimeSettings settings) => Results.Ok(settings.List()));

        control.MapGet("/settings/{key}", (string key, RuntimeSettings settings) =>
            settings.Get(key) is { } view ? Results.Ok(view) : Results.NotFound(new { error = $"unknown setting '{key}'" }));

        // The value is the request body, as plain text, so the CLI can send any string without escaping it into JSON.
        control.MapPut("/settings/{key}", async (string key, string? scope, HttpRequest request, RuntimeSettings settings) =>
        {
            if (!TryScope(scope, out var parsed))
            {
                return Results.BadRequest(new { error = $"scope must be cluster or instance, not '{scope}'" });
            }

            string value = await new StreamReader(request.Body).ReadToEndAsync();
            string? error = await settings.SetAsync(key, value, parsed);
            return error is null ? Results.Ok(settings.Get(key)) : Results.BadRequest(new { error });
        });

        control.MapDelete("/settings/{key}", async (string key, string? scope, RuntimeSettings settings) =>
        {
            if (!TryScope(scope, out var parsed))
            {
                return Results.BadRequest(new { error = $"scope must be cluster or instance, not '{scope}'" });
            }

            if (parsed == SettingScope.Cluster && !settings.ClusterShared)
            {
                return Results.BadRequest(new { error = "no shared settings store (Redis) is configured; use the instance scope" });
            }

            return await settings.RemoveAsync(key, parsed) ? Results.Ok(settings.Get(key)) : Results.NotFound(new { error = $"no {parsed} override for '{key}'" });
        });

        return routes;
    }

    private static bool TryScope(string? scope, out SettingScope parsed)
    {
        parsed = SettingScope.Cluster;
        return scope is null || Enum.TryParse(scope, ignoreCase: true, out parsed);
    }
}

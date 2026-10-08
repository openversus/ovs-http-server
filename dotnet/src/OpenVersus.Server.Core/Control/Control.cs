using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenVersus.Server.Core.Hosting;
using OpenVersus.Server.Core.Ops;
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

    /// <summary>Where a service's control socket is when <see cref="Socket"/> is empty; the CLI looks there by default.</summary>
    public static string DefaultSocketPath(string serviceName) => Path.Combine(Path.GetTempPath(), "openversus", $"{serviceName}.sock");

    internal string SocketPath(ServiceDefinition service) => Socket switch
    {
        "off" => "",
        "" => DefaultSocketPath(service.Name),
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

/// <summary>
/// The control API over HTTP: a thin mapping of <see cref="IControlService"/>, open to whoever the registered
/// <see cref="IControlAccessPolicy"/> allows (everyone else gets 404, as if it were not there).
/// </summary>
public static class ControlApi
{
    public static IEndpointRouteBuilder MapOpenVersusControl(this IEndpointRouteBuilder routes)
    {
        var control = routes.MapGroup("/control").AddEndpointFilter(async (context, next) =>
            context.HttpContext.RequestServices.GetRequiredService<IControlAccessPolicy>().Allows(context.HttpContext) ? await next(context) : Results.NotFound());

        control.MapGet("/status", (IControlService service) => Results.Ok(service.Status()));
        control.MapGet("/settings", (IControlService service) => Results.Ok(service.ListSettings()));
        control.MapGet("/cluster", async (IControlService service) => ToResult(await service.ClusterAsync()));
        control.MapGet("/settings/{key}", (string key, IControlService service) => ToResult(service.GetSetting(key)));

        // The value is the request body, as plain text, so the CLI can send any string without escaping it into JSON.
        control.MapPut("/settings/{key}", async (string key, string? scope, HttpRequest request, IControlService service) =>
        {
            if (!TryScope(scope, out var parsed))
            {
                return BadScope(scope);
            }

            string value = await new StreamReader(request.Body).ReadToEndAsync();
            return ToResult(await service.SetSettingAsync(key, value, parsed));
        });

        control.MapDelete("/settings/{key}", async (string key, string? scope, IControlService service) =>
            TryScope(scope, out var parsed) ? ToResult(await service.RemoveSettingAsync(key, parsed)) : BadScope(scope));

        // The live game's state: queues, who is connected, matches in progress, custom lobbies, player records.
        control.MapGet("/ops/queues", async (IOpsService ops) => ToResult(await ops.QueuesAsync()));
        control.MapGet("/ops/online", async (bool? players, IOpsService ops) => ToResult(await ops.OnlineAsync(players == true)));
        control.MapGet("/ops/matches", async (IOpsService ops) => ToResult(await ops.MatchesAsync()));
        control.MapGet("/ops/lobbies", async (IOpsService ops) => ToResult(await ops.LobbiesAsync()));
        control.MapGet("/ops/lobbies/{code}", async (string code, IOpsService ops) => ToResult(await ops.LobbyAsync(code)));
        control.MapGet("/ops/players/{who}", async (string who, IOpsService ops) => ToResult(await ops.FindPlayerAsync(who)));
        control.MapPut("/ops/players/{who}/name", async (string who, HttpRequest request, IOpsService ops) =>
            ToResult(await ops.RenamePlayerAsync(who, await new StreamReader(request.Body).ReadToEndAsync())));
        control.MapPost("/ops/players/{who}/disconnect", async (string who, IOpsService ops) => ToResult(await ops.DisconnectPlayerAsync(who)));
        control.MapPost("/ops/players/{who}/ban", async (string who, HttpRequest request, IOpsService ops) =>
            ToResult(await ops.BanPlayerAsync(who, await new StreamReader(request.Body).ReadToEndAsync())));

        // Any other path under /control is said to be one, with the service and its version (a CLI newer than the service
        // asks for endpoints it does not have): the game's fallback would answer it, asking for a game token.
        control.Map("/{**path}", (HttpContext http, IControlService service) =>
        {
            var status = service.Status();
            return Results.NotFound(new { error = $"no control endpoint {http.Request.Method} {http.Request.Path} on {status.Service} (version {status.Version ?? "unknown"})" });
        });

        return routes;
    }

    private static IResult ToResult<T>(ControlResult<T> result) =>
        result.Error is null ? Results.Ok(result.Value)
        : result.NotFound ? Results.NotFound(new { error = result.Error })
        : Results.BadRequest(new { error = result.Error });

    private static IResult BadScope(string? scope) => Results.BadRequest(new { error = $"scope must be cluster or instance, not '{scope}'" });

    private static bool TryScope(string? scope, out SettingScope parsed)
    {
        parsed = SettingScope.Cluster;
        return scope is null || Enum.TryParse(scope, ignoreCase: true, out parsed);
    }
}

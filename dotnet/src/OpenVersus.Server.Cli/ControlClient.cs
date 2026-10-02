using System.ComponentModel;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using OpenVersus.Server.Core.Control;
using OpenVersus.Server.Core.Hosting;
using OpenVersus.Server.Core.Ops;
using OpenVersus.Server.Core.Settings;
using Spectre.Console;
using Spectre.Console.Cli;

namespace OpenVersus.Server.Cli;

/// <summary>Which service to talk to, and how to reach it. Every command takes these.</summary>
public class ConnectionSettings : CommandSettings
{
    [CommandOption("-s|--service <SERVICE>")]
    [Description("The service: http, access, web, ws, matchmaking, matchflow or proxy (default: OVS_SERVICE, else http). Picks the default socket and port.")]
    public string Service { get; set; } = Environment.GetEnvironmentVariable(ServiceVariable) is { Length: > 0 } service ? service : "http";

    /// <summary>The default service: each service's container image sets it to its own.</summary>
    public const string ServiceVariable = "OVS_SERVICE";

    [CommandOption("--socket <PATH>")]
    [Description("The control socket to use instead of the service's default.")]
    public string? Socket { get; set; }

    [CommandOption("-p|--port <PORT>")]
    [Description("The control port on 127.0.0.1 to use instead of the socket (the way in on Windows).")]
    public int? Port { get; set; }

    [CommandOption("--json")]
    [Description("Print the service's answer as JSON, for scripts.")]
    public bool Json { get; set; }

    public override ValidationResult Validate() =>
        KnownServices.Find(Service) is null
            ? ValidationResult.Error($"unknown service '{Service}'; one of: {string.Join(", ", KnownServices.All.Select(s => s.Name))}")
            : ValidationResult.Success();
}

/// <summary>A service's readiness (<c>/health/ready</c>): the overall status, and each check's with why it failed.</summary>
public sealed record HealthView(string Status, Dictionary<string, HealthCheckView> Checks)
{
    public bool Ready => Status == "Healthy";
}

public sealed record HealthCheckView(string Status, string? Description);

/// <summary>The control API's answer to a request: the value, or why there is none.</summary>
public sealed record ControlReply<T>(T? Value, string? Error, bool Unreachable = false);

/// <summary>A client of one service's control API, over its Unix socket or its loopback port.</summary>
public sealed class ControlClient : IDisposable
{
    private readonly HttpClient _http;

    private ControlClient(HttpClient http, string where)
    {
        _http = http;
        Where = where;
    }

    /// <summary>How the service is being reached, for messages.</summary>
    public string Where { get; }

    /// <summary>
    /// An explicit --port or --socket wins; otherwise the service's default socket when it exists (and this OS has
    /// Unix sockets), else its default port.
    /// </summary>
    public static ControlClient For(ConnectionSettings settings)
    {
        var service = KnownServices.Find(settings.Service)!;
        if (settings.Port is int port)
        {
            return OverPort(port);
        }

        string socket = settings.Socket ?? ControlSettings.DefaultSocketPath(service.Name);
        if (settings.Socket is not null || (System.Net.Sockets.Socket.OSSupportsUnixDomainSockets && File.Exists(socket)))
        {
            return OverSocket(socket);
        }

        return OverPort(service.DefaultControlPort);
    }

    private static ControlClient OverPort(int port) =>
        new(new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(10) }, $"127.0.0.1:{port}");

    private static ControlClient OverSocket(string path) =>
        new(new HttpClient(new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        }) { BaseAddress = new Uri("http://localhost"), Timeout = TimeSpan.FromSeconds(10) }, path);

    /// <summary>The readiness answer, ready or not (503 is an answer here, not a failure).</summary>
    public async Task<ControlReply<HealthView>> HealthAsync()
    {
        try
        {
            using var response = await _http.GetAsync("/health/ready");
            if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.ServiceUnavailable
                && await response.Content.ReadFromJsonAsync<HealthView>(JsonSerializerOptions.Web) is { } health)
            {
                return new ControlReply<HealthView>(health, null);
            }

            return new ControlReply<HealthView>(default, $"the service answered {(int)response.StatusCode} {response.ReasonPhrase}");
        }
        catch (Exception e) when (e is HttpRequestException or SocketException or TaskCanceledException)
        {
            return new ControlReply<HealthView>(default, $"cannot reach the control API at {Where}: {e.InnerException?.Message ?? e.Message}", Unreachable: true);
        }
        catch (JsonException)
        {
            return new ControlReply<HealthView>(default, $"the readiness answer at {Where} is not the expected JSON");
        }
    }

    public Task<ControlReply<ClusterView>> ClusterAsync() => SendAsync<ClusterView>(HttpMethod.Get, "/control/cluster");

    public Task<ControlReply<ServiceStatus>> StatusAsync() => SendAsync<ServiceStatus>(HttpMethod.Get, "/control/status");

    public Task<ControlReply<SettingView[]>> ListAsync() => SendAsync<SettingView[]>(HttpMethod.Get, "/control/settings");

    public Task<ControlReply<SettingView>> GetAsync(string key) => SendAsync<SettingView>(HttpMethod.Get, $"/control/settings/{Uri.EscapeDataString(key)}");

    public Task<ControlReply<SettingView>> SetAsync(string key, string value, SettingScope scope) =>
        SendAsync<SettingView>(HttpMethod.Put, $"/control/settings/{Uri.EscapeDataString(key)}?scope={scope}", new StringContent(value));

    public Task<ControlReply<SettingView>> UnsetAsync(string key, SettingScope scope) =>
        SendAsync<SettingView>(HttpMethod.Delete, $"/control/settings/{Uri.EscapeDataString(key)}?scope={scope}");

    public Task<ControlReply<QueueView[]>> QueuesAsync() => SendAsync<QueueView[]>(HttpMethod.Get, "/control/ops/queues");

    public Task<ControlReply<OnlineView>> OnlineAsync(bool players) => SendAsync<OnlineView>(HttpMethod.Get, $"/control/ops/online?players={(players ? "true" : "false")}");

    public Task<ControlReply<MatchView[]>> MatchesAsync() => SendAsync<MatchView[]>(HttpMethod.Get, "/control/ops/matches");

    public Task<ControlReply<PlayerView>> PlayerAsync(string who) => SendAsync<PlayerView>(HttpMethod.Get, $"/control/ops/players/{Uri.EscapeDataString(who)}");

    public Task<ControlReply<PlayerView>> RenameAsync(string who, string name) =>
        SendAsync<PlayerView>(HttpMethod.Put, $"/control/ops/players/{Uri.EscapeDataString(who)}/name", new StringContent(name));

    public Task<ControlReply<DisconnectView>> DisconnectAsync(string who) =>
        SendAsync<DisconnectView>(HttpMethod.Post, $"/control/ops/players/{Uri.EscapeDataString(who)}/disconnect");

    private async Task<ControlReply<T>> SendAsync<T>(HttpMethod method, string path, HttpContent? body = null)
    {
        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(method, path) { Content = body };
            response = await _http.SendAsync(request);
        }
        catch (Exception e) when (e is HttpRequestException or SocketException or TaskCanceledException)
        {
            return new ControlReply<T>(default, $"cannot reach the control API at {Where}: {e.InnerException?.Message ?? e.Message}", Unreachable: true);
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                return new ControlReply<T>(await response.Content.ReadFromJsonAsync<T>(JsonSerializerOptions.Web), null);
            }

            string? error = null;
            try
            {
                error = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                // No error body: the status says enough.
            }

            return new ControlReply<T>(default, error ?? (response.StatusCode == HttpStatusCode.NotFound
                ? $"no control API at {Where} (is this a control listener?)"
                : $"the service answered {(int)response.StatusCode} {response.ReasonPhrase}"));
        }
    }

    public void Dispose() => _http.Dispose();
}

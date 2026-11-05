using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OpenVersus.Server.Core.Hosting;
using OpenVersus.Server.Core.Settings;

namespace OpenVersus.Server.Core.Control;

/// <summary>
/// Who may use the control API. The endpoints ask this and nothing else, so opening the API to remote
/// administration (a web UI, say) is a new policy (and a listener for it), not a change to the endpoints: e.g. one
/// that also accepts an authenticated administrator on an admin listener.
/// </summary>
public interface IControlAccessPolicy
{
    bool Allows(HttpContext context);
}

/// <summary>Today's policy: only requests that arrived on a control listener (the Unix socket or the loopback port).</summary>
public sealed class LocalControlListenersOnly : IControlAccessPolicy
{
    public bool Allows(HttpContext context) => ControlListeners.IsControl(context);
}

/// <summary>The service's state as the control API reports it.</summary>
public sealed record ServiceStatus(string Service, string Instance, string? Version, DateTimeOffset Started, long UptimeSeconds, bool SharedSettings);

/// <summary>What happened to a request: done (with its result), refused (and why), or about something that does not exist.</summary>
public sealed record ControlResult<T>(T? Value, string? Error, bool NotFound = false)
{
    public static ControlResult<T> Ok(T value) => new(value, null);

    public static ControlResult<T> Refused(string error) => new(default, error);

    public static ControlResult<T> Missing(string error) => new(default, error, NotFound: true);
}

/// <summary>
/// What the control API can do, whatever carries the request: the HTTP endpoints today, anything else later. The CLI
/// and any future web UI are clients of the endpoints, which are a thin mapping of this.
/// </summary>
public interface IControlService
{
    ServiceStatus Status();

    IEnumerable<SettingView> ListSettings();

    ControlResult<SettingView> GetSetting(string key);

    Task<ControlResult<SettingView>> SetSettingAsync(string key, string value, SettingScope scope);

    Task<ControlResult<SettingView>> RemoveSettingAsync(string key, SettingScope scope);

    /// <summary>Every instance of every service, from the registry in Redis (<see cref="InstanceRegistry"/>).</summary>
    Task<ControlResult<ClusterView>> ClusterAsync();
}

internal sealed class ControlService : IControlService
{
    private const string NoSharedStore = "no shared settings store (Redis) is configured; use the instance scope";
    private readonly ServiceDefinition _service;
    private readonly ServiceInstance _instance;
    private readonly RuntimeSettings _settings;
    private readonly IServiceProvider _services;

    public ControlService(ServiceDefinition service, ServiceInstance instance, RuntimeSettings settings, IServiceProvider services)
    {
        _service = service;
        _instance = instance;
        _settings = settings;
        _services = services;
    }

    public ServiceStatus Status() => new(
        _service.Name,
        _instance.Id,
        _instance.Version,
        _instance.Started,
        (long)(DateTimeOffset.UtcNow - _instance.Started).TotalSeconds,
        _settings.ClusterShared);

    public IEnumerable<SettingView> ListSettings() => _settings.List();

    public async Task<ControlResult<ClusterView>> ClusterAsync() =>
        _services.GetService<StackExchange.Redis.IConnectionMultiplexer>()?.GetDatabase() is { } redis
            ? ControlResult<ClusterView>.Ok(await InstanceRegistry.ReadAsync(redis, DateTimeOffset.UtcNow))
            : ControlResult<ClusterView>.Refused("this service has no Redis (REDIS), where the instances register: it knows only itself");

    public ControlResult<SettingView> GetSetting(string key) =>
        _settings.Get(key) is { } view ? ControlResult<SettingView>.Ok(view) : ControlResult<SettingView>.Missing($"unknown setting '{key}'");

    public async Task<ControlResult<SettingView>> SetSettingAsync(string key, string value, SettingScope scope)
    {
        string? error = await _settings.SetAsync(key, value, scope);
        return error is null ? ControlResult<SettingView>.Ok(_settings.Get(key)!) : ControlResult<SettingView>.Refused(error);
    }

    public async Task<ControlResult<SettingView>> RemoveSettingAsync(string key, SettingScope scope)
    {
        if (_settings.Get(key) is null)
        {
            return ControlResult<SettingView>.Missing($"unknown setting '{key}'");
        }

        if (scope == SettingScope.Cluster && !_settings.ClusterShared)
        {
            return ControlResult<SettingView>.Refused(NoSharedStore);
        }

        return await _settings.RemoveAsync(key, scope)
            ? ControlResult<SettingView>.Ok(_settings.Get(key)!)
            : ControlResult<SettingView>.Missing($"no {scope.ToString().ToLowerInvariant()} override for '{key}'");
    }
}

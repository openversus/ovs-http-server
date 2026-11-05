using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OpenVersus.Server.Core.Settings;

/// <summary>Which layer a change goes to: every replica (through the shared store), or only this process.</summary>
public enum SettingScope
{
    Cluster,
    Instance,
}

/// <summary>The two layers the service changes while running; see <see cref="OverrideLayer"/>.</summary>
public sealed record SettingsLayers(OverrideLayer Cluster, OverrideLayer Instance);

/// <summary>A setting as the control API shows it. Values of secret settings are never included.</summary>
public sealed record SettingView(string Key, string Type, string Description, string? Value, string? Cluster, string? Instance, bool RestartRequired, bool Secret);

/// <summary>
/// Reads and changes settings while the service runs: what the control API (and so the CLI) calls. Every change is
/// validated against the setting's options class first, and a setting that only takes effect at startup is refused.
/// </summary>
public sealed class RuntimeSettings
{
    private const string Hidden = "(secret)";
    private readonly SettingsCatalog _catalog;
    private readonly IConfiguration _config;
    private readonly SettingsLayers _layers;
    private readonly IClusterSettingsStore _store;
    private readonly ILogger<RuntimeSettings> _log;

    public RuntimeSettings(SettingsCatalog catalog, IConfiguration config, SettingsLayers layers, IClusterSettingsStore store, ILogger<RuntimeSettings> log)
    {
        _catalog = catalog;
        _config = config;
        _layers = layers;
        _store = store;
        _log = log;
    }

    public bool ClusterShared => _store.IsShared;

    public IEnumerable<SettingView> List() => _catalog.All.Select(View);

    public SettingView? Get(string key) => _catalog.TryGet(key, out var setting) ? View(setting) : null;

    /// <summary>Null when the change was made; otherwise why it was refused.</summary>
    public async Task<string?> SetAsync(string key, string value, SettingScope scope)
    {
        if (!_catalog.TryGet(key, out var setting))
        {
            return $"unknown setting '{key}'";
        }

        if (setting.RestartRequired)
        {
            return $"{setting.Key} only takes effect at startup; set it in the configuration or environment and restart";
        }

        string? error = _catalog.Validate(setting.Key, value, _config);
        if (error is not null)
        {
            return error;
        }

        if (scope == SettingScope.Cluster)
        {
            if (!_store.IsShared)
            {
                return "no shared settings store (Redis) is configured; use the instance scope";
            }

            await _store.SetAsync(setting.Key, value);
            // The announcement brings every replica, this one included, back to the store's contents; setting it here
            // as well means this one does not wait for its own announcement.
            _layers.Cluster.SetOverride(setting.Key, value);
        }
        else
        {
            _layers.Instance.SetOverride(setting.Key, value);
        }

        _log.LogInformation("Setting {Key} = {Value} ({Scope})", setting.Key, setting.Secret ? Hidden : value, scope);
        return null;
    }

    /// <summary>Removes an override, so the value falls back to the layer below. False if there was none.</summary>
    public async Task<bool> RemoveAsync(string key, SettingScope scope)
    {
        if (!_catalog.TryGet(key, out var setting))
        {
            return false;
        }

        bool removed;
        if (scope == SettingScope.Cluster)
        {
            removed = _store.IsShared && await _store.RemoveAsync(setting.Key);
            _layers.Cluster.RemoveOverride(setting.Key);
        }
        else
        {
            removed = _layers.Instance.RemoveOverride(setting.Key);
        }

        if (removed)
        {
            _log.LogInformation("Setting {Key} override removed ({Scope})", setting.Key, scope);
        }

        return removed;
    }

    /// <summary>Makes the cluster layer equal to the shared store.</summary>
    public async Task ReloadClusterAsync()
    {
        _layers.Cluster.ReplaceAll(await _store.LoadAsync());
    }

    private SettingView View(SettingInfo setting)
    {
        string? value = _config[setting.Key] ?? DefaultOf(setting);
        _layers.Cluster.Values.TryGetValue(setting.Key, out string? cluster);
        _layers.Instance.Values.TryGetValue(setting.Key, out string? instance);
        return setting.Secret
            ? new SettingView(setting.Key, setting.TypeName, setting.Description, value is null ? null : Hidden, cluster is null ? null : Hidden, instance is null ? null : Hidden, setting.RestartRequired, true)
            : new SettingView(setting.Key, setting.TypeName, setting.Description, value, cluster, instance, setting.RestartRequired, false);
    }

    private static string? DefaultOf(SettingInfo setting) =>
        setting.Property.GetValue(Activator.CreateInstance(setting.OptionsType))?.ToString();
}

/// <summary>Loads the shared settings before the service starts serving, and rereads them whenever any replica changes one.</summary>
internal sealed class ClusterSettingsSync : IHostedService
{
    private readonly RuntimeSettings _settings;
    private readonly IClusterSettingsStore _store;
    private readonly ILogger<ClusterSettingsSync> _log;

    public ClusterSettingsSync(RuntimeSettings settings, IClusterSettingsStore store, ILogger<ClusterSettingsSync> log)
    {
        _settings = settings;
        _store = store;
        _log = log;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_store.IsShared)
        {
            _log.LogWarning("No shared settings store (REDIS is not set): settings can only be changed for this instance");
            return;
        }

        try
        {
            await _settings.ReloadClusterAsync();
            await _store.SubscribeAsync(async () =>
            {
                try
                {
                    await _settings.ReloadClusterAsync();
                }
                catch (Exception e)
                {
                    _log.LogError(e, "Could not reread the shared settings");
                }
            });
        }
        catch (Exception e)
        {
            // The service still runs on its configuration and environment; it just misses the shared overrides.
            _log.LogError(e, "Could not load the shared settings from Redis; running without them");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public static class SettingsServiceCollectionExtensions
{
    /// <summary>
    /// Registers an options class as settings under <paramref name="section"/>: bound from configuration, validated
    /// with its data annotations, listed by the control API, and changeable while running (read it through
    /// <c>IOptionsMonitor&lt;T&gt;</c>, never into a field, or a change will not reach it).
    /// </summary>
    public static WebApplicationBuilder AddSetting<T>(this WebApplicationBuilder builder, string section) where T : class
    {
        var catalog = builder.Services.Select(d => d.ImplementationInstance).OfType<SettingsCatalog>().SingleOrDefault()
            ?? throw new InvalidOperationException("AddSetting needs OpenVersusHost.CreateBuilder's settings catalog");
        catalog.Register(typeof(T), section);
        builder.Services.AddOptions<T>().Bind(builder.Configuration.GetSection(section)).ValidateDataAnnotations().ValidateOnStart();
        return builder;
    }
}

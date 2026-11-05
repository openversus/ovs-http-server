using Microsoft.Extensions.Configuration;

namespace OpenVersus.Server.Core.Settings;

/// <summary>
/// A configuration layer the service changes while running. Two are added on top of appsettings and the environment:
/// the cluster layer, which mirrors the settings every replica shares (kept in Redis), and above it the instance layer,
/// which only this process has. Changing either raises the configuration's reload token, so everything reading
/// settings through <c>IOptionsMonitor</c> sees the new value at once.
/// </summary>
public sealed class OverrideLayer : ConfigurationProvider, IConfigurationSource
{
    private readonly object _lock = new();

    public OverrideLayer(string name)
    {
        Name = name;
    }

    public string Name { get; }

    public IConfigurationProvider Build(IConfigurationBuilder builder) => this;

    /// <summary>The keys this layer currently overrides, with their values.</summary>
    public IReadOnlyDictionary<string, string?> Values
    {
        get
        {
            lock (_lock)
            {
                return new Dictionary<string, string?>(Data, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    public void SetOverride(string key, string value)
    {
        lock (_lock)
        {
            Data[key] = value;
        }

        OnReload();
    }

    public bool RemoveOverride(string key)
    {
        bool removed;
        lock (_lock)
        {
            removed = Data.Remove(key);
        }

        if (removed)
        {
            OnReload();
        }

        return removed;
    }

    /// <summary>Replaces everything in the layer at once (the cluster layer, after reading Redis), with one reload.</summary>
    public void ReplaceAll(IReadOnlyDictionary<string, string> values)
    {
        lock (_lock)
        {
            Data = new Dictionary<string, string?>(values.ToDictionary(kv => kv.Key, kv => (string?)kv.Value), StringComparer.OrdinalIgnoreCase);
        }

        OnReload();
    }

    public override string ToString() => $"OverrideLayer({Name})";
}

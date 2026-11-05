using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Settings;

/// <summary>Where the settings every replica shares are kept, and how a replica hears that they changed.</summary>
public interface IClusterSettingsStore
{
    /// <summary>False when there is nowhere shared to keep them (no Redis configured): cluster changes then fail.</summary>
    bool IsShared { get; }

    Task<IReadOnlyDictionary<string, string>> LoadAsync();

    Task SetAsync(string key, string value);

    Task<bool> RemoveAsync(string key);

    /// <summary>Calls <paramref name="changed"/> whenever any replica changes a shared setting.</summary>
    Task SubscribeAsync(Func<Task> changed);
}

/// <summary>No shared store: nothing is shared, and a cluster change is refused rather than kept only here.</summary>
public sealed class LocalOnlySettingsStore : IClusterSettingsStore
{
    public bool IsShared => false;

    public Task<IReadOnlyDictionary<string, string>> LoadAsync() => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());

    public Task SetAsync(string key, string value) => throw new InvalidOperationException("no shared settings store (Redis) is configured; use the instance scope");

    public Task<bool> RemoveAsync(string key) => throw new InvalidOperationException("no shared settings store (Redis) is configured; use the instance scope");

    public Task SubscribeAsync(Func<Task> changed) => Task.CompletedTask;
}

/// <summary>
/// Shared settings in Redis: one hash holds them, and a change is announced on a channel so every replica rereads
/// the hash. Rereading the whole hash rather than applying the one change keeps every replica equal to the hash
/// even when announcements arrive out of order.
/// </summary>
public sealed class RedisSettingsStore : IClusterSettingsStore
{
    public const string HashKey = "ovs:settings";
    public static readonly RedisChannel Channel = RedisChannel.Literal("ovs:settings:changed");

    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisSettingsStore> _log;

    public RedisSettingsStore(IConnectionMultiplexer redis, ILogger<RedisSettingsStore> log)
    {
        _redis = redis;
        _log = log;
    }

    public bool IsShared => true;

    public async Task<IReadOnlyDictionary<string, string>> LoadAsync()
    {
        var entries = await _redis.GetDatabase().HashGetAllAsync(HashKey);
        return entries.ToDictionary(e => e.Name.ToString(), e => e.Value.ToString(), StringComparer.OrdinalIgnoreCase);
    }

    public async Task SetAsync(string key, string value)
    {
        await _redis.GetDatabase().HashSetAsync(HashKey, key, value);
        await _redis.GetSubscriber().PublishAsync(Channel, key);
    }

    public async Task<bool> RemoveAsync(string key)
    {
        bool removed = await _redis.GetDatabase().HashDeleteAsync(HashKey, key);
        await _redis.GetSubscriber().PublishAsync(Channel, key);
        return removed;
    }

    public Task SubscribeAsync(Func<Task> changed) =>
        _redis.GetSubscriber().SubscribeAsync(Channel, (channel, key) =>
        {
            _log.LogDebug("Shared setting {Key} changed; rereading", key.ToString());
            changed();
        });
}

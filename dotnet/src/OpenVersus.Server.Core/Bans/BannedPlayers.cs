using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Bans;

/// <summary>
/// The banned player ids (Redis bans:player), held in memory by every service so a session token that is still valid
/// is refused at once, by the HTTP token check and by the realtime gateway, with no Redis read per request. A ban's
/// bans:changed message adds its player at once; the whole set is reread then and every <see cref="Refresh"/>, so a
/// missed message or a ban written straight into Redis still arrives.
/// </summary>
public sealed class BannedPlayers(IServiceProvider services, ILogger<BannedPlayers> log) : BackgroundService
{
    public const string Key = "bans:player";

    /// <summary>Published with the player id whenever a ban is made: the caches add them, the sweep looks for the person's other connections.</summary>
    public const string ChangedChannel = "bans:changed";
    public static readonly TimeSpan Refresh = TimeSpan.FromSeconds(60);

    private volatile HashSet<string> _ids = [];
    private readonly SemaphoreSlim _changed = new(0);

    public bool Contains(string playerId) => _ids.Contains(playerId);

    /// <summary>Adds a player at once (a ban's message), before the next full read.</summary>
    public void Remember(string playerId) => _ids = new HashSet<string>(_ids) { playerId };

    /// <summary>Drops a player until the next full read (which brings them back if they are still in Redis). For tests.</summary>
    public void Forget(string playerId) => _ids = [.. _ids.Where(id => id != playerId)];

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (services.GetService<IConnectionMultiplexer>() is not { } redis)
        {
            return;
        }

        bool subscribed = false;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                if (!subscribed)
                {
                    await redis.GetSubscriber().SubscribeAsync(RedisChannel.Literal(ChangedChannel), (_, player) =>
                    {
                        if (player.HasValue && player.ToString() is { Length: > 0 } id)
                        {
                            Remember(id);
                        }

                        _changed.Release();
                    });
                    subscribed = true;
                }

                var members = await redis.GetDatabase().SetMembersAsync(Key);
                _ids = [.. members.Select(m => m.ToString())];
            }
            catch (Exception e) when (e is RedisException or TimeoutException)
            {
                log.LogWarning(e, "Reading the banned players failed; keeping the {Count} known, retrying", _ids.Count);
            }

            try
            {
                await _changed.WaitAsync(subscribed ? Refresh : TimeSpan.FromSeconds(5), stop);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}

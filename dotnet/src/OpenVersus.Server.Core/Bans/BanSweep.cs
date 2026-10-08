using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Hosting;
using OpenVersus.Server.Core.Realtime;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Bans;

/// <summary>
/// Finds banned people who are online: at start, when a ban file changes (read once it has settled), when a ban is made
/// (bans:changed: the person's other accounts on a shared identifier), and every <see cref="Backstop"/>. Each online
/// player is checked through every identifier their account has; one who matches is cut off and their player id banned
/// (source sweep, with the identifier that matched). Their other identifiers are left alone: the matched one is banned
/// already. One replica sweeps at a time (a Redis lock); the access service runs it.
/// </summary>
internal sealed class BanSweep(IServiceProvider services, IOptionsMonitor<BanSettings> settings, IBanService bans, IPersonBans personBans,
    ServiceInstance instance, TimeProvider time, ILogger<BanSweep> log) : BackgroundService
{
    public static readonly TimeSpan Poll = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan Backstop = TimeSpan.FromSeconds(60);
    private const string LockKey = "bans:sweep:lock";
    private static readonly TimeSpan s_lockTime = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _changed = new(0);

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (services.GetService<IConnectionMultiplexer>() is not { } multiplexer)
        {
            return;
        }

        bool subscribed = false;
        string? swept = null;
        var lastSweep = DateTimeOffset.MinValue;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                if (!subscribed)
                {
                    await multiplexer.GetSubscriber().SubscribeAsync(RedisChannel.Literal(BannedPlayers.ChangedChannel), (_, _) => _changed.Release());
                    subscribed = true;
                }

                var now = time.GetUtcNow();
                var (stamps, settled) = Stamps(settings.CurrentValue, now.UtcDateTime);
                bool filesChanged = stamps != swept;
                bool signalled = _changed.CurrentCount > 0;
                if ((filesChanged && settled) || signalled || now - lastSweep >= Backstop)
                {
                    while (_changed.CurrentCount > 0)
                    {
                        await _changed.WaitAsync(stop);
                    }

                    if (filesChanged && swept is not null)
                    {
                        // The auto-ban file may have gained records another service wrote: into the sets every service reads.
                        await BanLoader.LoadAsync(services.GetService<IMongoDatabase>(), multiplexer.GetDatabase(), settings.CurrentValue, log, stop);
                    }

                    await SweepAsync(multiplexer.GetDatabase(), stop);
                    lastSweep = now;
                    if (settled)
                    {
                        swept = stamps;
                    }
                }
            }
            catch (Exception e) when (e is RedisException or MongoException or TimeoutException)
            {
                log.LogWarning(e, "The ban sweep failed; trying again");
            }

            try
            {
                if (await _changed.WaitAsync(Poll, stop))
                {
                    // Left for the next round to see.
                    _changed.Release();
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Each ban file's write time, and whether none changed within the settle time (a file still being written).</summary>
    private static (string Stamps, bool Settled) Stamps(BanSettings current, DateTime now)
    {
        var times = BanService.Files(current).Select(f => string.IsNullOrWhiteSpace(f) || !File.Exists(f) ? DateTime.MinValue : File.GetLastWriteTimeUtc(f)).ToList();
        return (string.Join(",", times.Select(t => t.Ticks)), times.All(t => now - t >= NameRules.SettleTime));
    }

    internal async Task<int> SweepAsync(IDatabase redis, CancellationToken ct)
    {
        if (services.GetService<IMongoDatabase>() is not { } mongo)
        {
            return 0;
        }

        if (!await redis.StringSetAsync(LockKey, instance.Id, s_lockTime, When.NotExists))
        {
            return 0;
        }

        int caught = 0;
        try
        {
            var online = (await redis.SetMembersAsync("online_players")).Select(m => ObjectId.TryParse(m.ToString(), out var id) ? id : ObjectId.Empty)
                .Where(id => id != ObjectId.Empty).ToList();
            if (online.Count == 0)
            {
                return 0;
            }

            using var cursor = await mongo.GetCollection<BsonDocument>(Access.PlayerRecord.Collection).FindAsync(Builders<BsonDocument>.Filter.In("_id", online), cancellationToken: ct);
            foreach (var player in await cursor.ToListAsync(ct))
            {
                string id = player["_id"].AsObjectId.ToString();
                if (await redis.SetContainsAsync(BannedPlayers.Key, id))
                {
                    // Banned already and still online (a disconnect that did not land): again.
                    await PlayerMessages.DisconnectAsync(redis, new System.Text.Json.Nodes.JsonObject { ["playerId"] = id });
                    continue;
                }

                if (await bans.FindAsync(BanIdentifiers.OfPlayer(player)) is { } match)
                {
                    caught++;
                    await personBans.BanAsync(new BanRequest(id, $"banned {match.Kind.ToString().ToLowerInvariant()}", "sweep",
                        MatchedList: match.Source, MatchedTerm: match.Value, WholePerson: false), ct);
                }
            }
        }
        finally
        {
            await redis.ScriptEvaluateAsync("if redis.call('GET', KEYS[1]) == ARGV[1] then return redis.call('DEL', KEYS[1]) end return 0",
                [LockKey], [instance.Id]);
        }

        return caught;
    }
}

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
/// Keeps the bans current and finds banned people who are online. A ban file that changed is imported into Mongo once it
/// has settled; every round loads the active bans into Redis (a Redis flushed while running is filled again). Rounds run
/// at start, on a file change, when a ban is made or values are loaded (bans:changed; not a lift), and every
/// <see cref="Backstop"/>. Each online
/// player is checked through every identifier their account has; one who matches is banned as a person, every identifier
/// of theirs (source sweep, with the identifier that matched), and cut off. One replica sweeps at a time (a Redis lock);
/// the access service runs it.
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
                    await multiplexer.GetSubscriber().SubscribeAsync(RedisChannel.Literal(BannedPlayers.ChangedChannel), (_, message) =>
                    {
                        if (BanEvent.Parse(message).Kind != BanEventKind.Lifted)
                        {
                            _changed.Release();
                        }
                    });
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

                    var redis = multiplexer.GetDatabase();
                    if (services.GetService<IMongoDatabase>() is { } mongo)
                    {
                        // The first round's files were imported by BanLoader at start.
                        if (filesChanged && settled && swept is not null)
                        {
                            await BanStore.ImportAsync(mongo, settings.CurrentValue, now.UtcDateTime, log, stop);
                        }

                        if (await BanStore.LoadAsync(mongo, redis, log, stop) > 0)
                        {
                            await redis.PublishAsync(RedisChannel.Literal(BannedPlayers.ChangedChannel), BanEvent.Loaded.ToString());
                        }
                    }

                    await SweepAsync(redis, stop);
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
        var times = BanStore.Files(current).Select(f => f.Path).Select(f => string.IsNullOrWhiteSpace(f) || !File.Exists(f) ? DateTime.MinValue : File.GetLastWriteTimeUtc(f)).ToList();
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
                        MatchedList: match.Source, MatchedTerm: match.Value), ct);
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

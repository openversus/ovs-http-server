using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Compat;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Bans;

// Where bans come from, all merged:
//   files   the TS server's ban files (IP_BANS_FILE, CIDR_BANS_FILE, HASHBANS_FILE), read when they exist, reread when
//           they change. Same format as there: one entry per line, blank lines and lines starting with # skipped,
//           entries not trimmed. The TS server also names STEAMID_BANS_FILE and EPICID_BANS_FILE, but reads
//           HASHBANS_FILE for both, so only these three were ever in effect.
//   Redis   sets bans:ip, bans:cidr, bans:id (new; the TS server has none). Shared by every replica at once.
//   Mongo   collection bans: { kind: "ip" | "cidr" | "id", value, reason?, created_at? } (new). The long-term store:
//           loaded into the Redis sets at startup. Loading only adds, so a ban put straight into Redis survives a
//           replica's restart; lifting a ban means removing it from both.
// As in the TS server, the one question asked is whether a value (the /access IP) is banned, and it is checked against
// every list: IPs, ids, and the CIDR blocks.

/// <summary>Ban sources. The TS server's variable names fill the file paths.</summary>
public sealed class BanSettings
{
    [Description("The TS server's IP ban file (IP_BANS_FILE). Read when it exists. A relative path is relative to the working directory.")]
    public string? IpFile { get; set; } = "../data/bans.txt";

    [Description("The TS server's CIDR ban file (CIDR_BANS_FILE), IPv4 blocks like 203.0.113.0/24; a line without a prefix is one address.")]
    public string? CidrFile { get; set; } = "../data/cidr_bans.txt";

    [Description("The TS server's id ban file (HASHBANS_FILE): Steam ids, Epic ids, hashes. The TS server checks the /access IP against it too.")]
    public string? IdFile { get; set; } = "../data/hashbans.txt";
}

public interface IBanService
{
    /// <summary>Whether <paramref name="value"/> (an IP, as /access asks) is banned by any source.</summary>
    Task<bool> IsBannedAsync(string value);
}

internal sealed class BanService(IServiceProvider services, IOptionsMonitor<BanSettings> settings, ILogger<BanService> log) : IBanService
{
    public const string IpKey = "bans:ip";
    public const string CidrKey = "bans:cidr";
    public const string IdKey = "bans:id";

    private readonly ConcurrentDictionary<string, BanFile> _files = new();

    public async Task<bool> IsBannedAsync(string value)
    {
        var current = settings.CurrentValue;
        var ips = Read(current.IpFile);
        var ids = Read(current.IdFile);
        var cidrs = Read(current.CidrFile);
        if (ips.Contains(value) || ids.Contains(value) || cidrs.Any(c => InCidr(value, c)))
        {
            return true;
        }

        if (services.GetService<IConnectionMultiplexer>()?.GetDatabase() is not { } redis)
        {
            return false;
        }

        if (await redis.SetContainsAsync(IpKey, value) || await redis.SetContainsAsync(IdKey, value))
        {
            return true;
        }

        return (await redis.SetMembersAsync(CidrKey)).Any(c => InCidr(value, c.ToString()));
    }

    /// <summary>
    /// Whether <paramref name="ip"/> is in <paramref name="block"/>. IPv4 only, as in the TS server; a block without a
    /// prefix length is that one address (there, NaN makes the mask all ones). Anything unreadable matches nothing.
    /// </summary>
    internal static bool InCidr(string ip, string block)
    {
        string[] parts = block.Split('/');
        int prefix = 32;
        if (parts.Length > 2 || (parts.Length == 2 && (!int.TryParse(parts[1], out prefix) || prefix is < 0 or > 32)))
        {
            return false;
        }

        if (!TryIPv4(ip, out uint address) || !TryIPv4(parts[0], out uint network))
        {
            return false;
        }

        uint mask = prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);
        return (address & mask) == (network & mask);
    }

    private static bool TryIPv4(string text, out uint value)
    {
        value = 0;
        if (!IPAddress.TryParse(text, out var address) || address.AddressFamily != AddressFamily.InterNetwork || text.Count(c => c == '.') != 3)
        {
            return false;
        }

        byte[] bytes = address.GetAddressBytes();
        value = (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);
        return true;
    }

    private IReadOnlyCollection<string> Read(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return [];
        }

        var file = _files.GetOrAdd(Path.GetFullPath(path), full => new BanFile(full));
        return file.Entries(log);
    }

    private sealed class BanFile(string path)
    {
        private readonly Lock _lock = new();
        private DateTime _stamp = DateTime.MinValue;
        private bool _missingLogged;
        private HashSet<string> _entries = [];

        public IReadOnlyCollection<string> Entries(ILogger log)
        {
            lock (_lock)
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    if (!_missingLogged)
                    {
                        log.LogInformation("Ban file {Path} does not exist; no bans from it", path);
                        _missingLogged = true;
                    }

                    _entries = [];
                    _stamp = DateTime.MinValue;
                    return _entries;
                }

                _missingLogged = false;
                if (info.LastWriteTimeUtc != _stamp)
                {
                    _entries = [.. File.ReadAllText(path).Split('\n')
                        .Select(line => line.EndsWith('\r') ? line[..^1] : line)
                        .Where(line => Js.Trim(line).Length > 0 && !line.StartsWith('#'))];
                    _stamp = info.LastWriteTimeUtc;
                    log.LogInformation("Ban file {Path}: {Count} entries", path, _entries.Count);
                }

                return _entries;
            }
        }
    }
}

/// <summary>At startup, adds the bans stored in Mongo to the Redis sets every replica reads.</summary>
internal sealed class BanLoader(IServiceProvider services, ILogger<BanLoader> log) : IHostedService
{
    public const string Collection = "bans";

    public async Task StartAsync(CancellationToken ct)
    {
        if (services.GetService<IMongoDatabase>() is not { } mongo || services.GetService<IConnectionMultiplexer>() is not { } multiplexer)
        {
            return;
        }

        try
        {
            var redis = multiplexer.GetDatabase();
            var counts = new Dictionary<string, int>();
            using var cursor = await mongo.GetCollection<BsonDocument>(Collection).FindAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: ct);
            var byKey = new Dictionary<string, List<RedisValue>>();
            await cursor.ForEachAsync(ban =>
            {
                string? key = ban.GetValue("kind", BsonNull.Value).ToString() switch
                {
                    "ip" => BanService.IpKey,
                    "cidr" => BanService.CidrKey,
                    "id" => BanService.IdKey,
                    _ => null,
                };
                if (key is not null && ban.GetValue("value", BsonNull.Value) is { IsString: true } value)
                {
                    (byKey.TryGetValue(key, out var list) ? list : byKey[key] = []).Add(value.AsString);
                }
            }, ct);

            foreach (var (key, values) in byKey)
            {
                await redis.SetAddAsync(key, [.. values]);
                counts[key] = values.Count;
            }

            log.LogInformation("Loaded bans from Mongo into Redis: {Counts}", counts.Count == 0 ? "none" : string.Join(", ", counts.Select(c => $"{c.Key} {c.Value}")));
        }
        catch (Exception e) when (e is MongoException or RedisException or TimeoutException)
        {
            // A replica that cannot load them still serves: the other replicas loaded them, and the sets persist.
            log.LogError(e, "Loading bans from Mongo into Redis failed");
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}

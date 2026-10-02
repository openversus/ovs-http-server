using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Hosting;

// Every instance of every service announces itself in Redis, so that any one of them can show the whole cluster
// (ovs-ctl health): ovs:instance:{id} (an InstanceReport as JSON, EX 20 s) rewritten every 5 s, and ovs:instances (a
// sorted set of "{service}/{id}" by last heartbeat, ms) to list them. An instance that stops cleanly says so (Stopped,
// kept 10 minutes). One that dies stops writing: its key expires and the listing shows it Missing, with its last
// heartbeat, for 10 minutes. Its readiness is its own /health/ready checks (stores only, ServiceStores), run at each
// heartbeat: one Redis round trip and, where Mongo is needed, one ping per instance every 5 s.

/// <summary>One readiness check of an instance, as its last heartbeat saw it.</summary>
public sealed record InstanceCheck(string Status, string? Description);

/// <summary>An instance as the registry knows it. <see cref="State"/>: Ready, NotReady, Stopped, or Missing (no heartbeat lately).</summary>
public sealed record InstanceReport(string Service, string Instance, string? Version, DateTimeOffset Started, DateTimeOffset Seen, string State,
    Dictionary<string, InstanceCheck> Checks);

/// <summary>Every instance the registry knows, and the services there are.</summary>
public sealed record ClusterView(DateTimeOffset At, IReadOnlyList<InstanceReport> Instances, IReadOnlyList<string> Services);

public static class InstanceRegistry
{
    public const string Index = "ovs:instances";
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan Remembered = TimeSpan.FromMinutes(10);

    public static string Key(string instance) => $"ovs:instance:{instance}";

    internal static string Member(string service, string instance) => $"{service}/{instance}";

    internal static async Task WriteAsync(IDatabase redis, InstanceReport report, TimeSpan ttl)
    {
        var batch = redis.CreateBatch();
        var set = batch.StringSetAsync(Key(report.Instance), JsonSerializer.Serialize(report, JsonSerializerOptions.Web), ttl);
        var add = batch.SortedSetAddAsync(Index, Member(report.Service, report.Instance), report.Seen.ToUnixTimeMilliseconds());
        batch.Execute();
        await Task.WhenAll(set, add);
    }

    /// <summary>Every instance heard from in the last <see cref="Remembered"/>, by service then instance.</summary>
    public static async Task<ClusterView> ReadAsync(IDatabase redis, DateTimeOffset now)
    {
        await redis.SortedSetRemoveRangeByScoreAsync(Index, double.NegativeInfinity, (now - Remembered).ToUnixTimeMilliseconds());
        var members = await redis.SortedSetRangeByRankWithScoresAsync(Index);
        var reports = new List<InstanceReport>();
        if (members.Length > 0)
        {
            var ids = members.Select(m => m.Element.ToString().Split('/', 2)).ToArray();
            var values = await redis.StringGetAsync(ids.Select(id => (RedisKey)Key(id.Length > 1 ? id[1] : id[0])).ToArray());
            for (int i = 0; i < members.Length; i++)
            {
                var report = values[i].HasValue ? JsonSerializer.Deserialize<InstanceReport>(values[i].ToString(), JsonSerializerOptions.Web) : null;
                reports.Add(report ?? new InstanceReport(ids[i][0], ids[i].Length > 1 ? ids[i][1] : "", null, default,
                    DateTimeOffset.FromUnixTimeMilliseconds((long)members[i].Score), "Missing", []));
            }
        }

        return new ClusterView(now, reports.OrderBy(r => r.Service, StringComparer.Ordinal).ThenBy(r => r.Instance, StringComparer.Ordinal).ToList(),
            KnownServices.All.Select(s => s.Name).ToList());
    }
}

/// <summary>Writes this instance into the registry every <see cref="InstanceRegistry.Interval"/>, and Stopped when it stops.</summary>
internal sealed class InstanceHeartbeat(IServiceProvider services, ServiceDefinition service, ServiceInstance instance, HealthCheckService health,
    ILogger<InstanceHeartbeat> log) : BackgroundService
{
    // Resolved while the host starts: a host can be stopped after its services are disposed (a test server's is).
    private readonly IConnectionMultiplexer? _redis = services.GetService<IConnectionMultiplexer>();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (_redis?.GetDatabase() is { } redis)
            {
                try
                {
                    var ready = await health.CheckHealthAsync(c => c.Tags.Contains("ready"), stoppingToken);
                    await InstanceRegistry.WriteAsync(redis, Report(ready.Status == HealthStatus.Healthy ? "Ready" : "NotReady",
                        ready.Entries.ToDictionary(e => e.Key, e => new InstanceCheck(e.Value.Status.ToString(), e.Value.Description))), InstanceRegistry.Ttl);
                }
                catch (Exception e) when (e is RedisException or TimeoutException)
                {
                    log.LogDebug("Instance heartbeat not written: {Error}", e.Message);
                }
            }

            try
            {
                await Task.Delay(InstanceRegistry.Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        if (_redis is { IsConnected: true } redis)
        {
            try
            {
                await InstanceRegistry.WriteAsync(redis.GetDatabase(), Report("Stopped", []), InstanceRegistry.Remembered);
            }
            catch (Exception e) when (e is RedisException or TimeoutException or ObjectDisposedException)
            {
                log.LogDebug("Instance stop not written: {Error}", e.Message);
            }
        }
    }

    private InstanceReport Report(string state, Dictionary<string, InstanceCheck> checks) =>
        new(service.Name, instance.Id, instance.Version, instance.Started, DateTimeOffset.UtcNow, state, checks);
}

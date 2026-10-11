using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Arenas;

/// <summary>
/// The arenas service's deadline loop: every <see cref="Interval"/>, the runs whose phase's deadline has passed are acted on
/// (<see cref="IArenaService.DeadlinesAsync"/>). Each replica runs it; the run's compare-and-set lets one of them act.
/// </summary>
internal sealed class ArenaDeadlines(IServiceProvider services, IArenaService arenas, TimeProvider time, ILogger<ArenaDeadlines> log) : BackgroundService
{
    internal static TimeSpan Interval { get; set; } = TimeSpan.FromMilliseconds(500);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (services.GetService<IConnectionMultiplexer>() is null)
        {
            log.LogWarning("Arena deadlines are not acted on from here: this service has no Redis (REDIS)");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await arenas.DeadlinesAsync(stoppingToken);
            }
            catch (Exception e) when (e is RedisException or TimeoutException or InvalidOperationException or System.Text.Json.JsonException or ArenaRunConflictException)
            {
                log.LogError("Arena deadlines: {Error}", e.Message);
            }

            try
            {
                await Task.Delay(Interval, time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}

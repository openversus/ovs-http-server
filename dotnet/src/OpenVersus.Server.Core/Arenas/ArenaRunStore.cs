using System.Text.Json;
using System.Text.Json.Serialization;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Arenas;

// Where Arena runs live (ArenaRun): one JSON document per run, and the deadlines the arenas service's loop acts on.
//
//   arena_run:{id}         the run (its id is the Arena lobby's, the ArenaId the game sends back); EX Ttl, renewed by
//                          every write
//   arena_run:deadlines    a sorted set: run id -> its DeadlineMs, for the runs that have one (character select, shop)
//
// A change (UpdateAsync) is a compare-and-set, as LobbyStore's: the run is read, the transition runs in C#, and the run
// and its deadline are written in one transaction only if the record is still what was read; otherwise the transition
// runs again on the new value. A transition can therefore run more than once: it must change only the run it is given
// (ArenaRun's are pure). Two replicas of the service acting on the same deadline both try; one wins, and the other's
// Tick finds the phase moved on and is refused.
//
// Ttl: the longest a live run goes unwritten is one round's node match (RoundSeconds of play plus faceoff and loading,
// minutes); an hour covers that many times over. A run nobody writes for an hour is abandoned and expires; its deadline
// entry, if any, is removed by the first loop that finds the run gone.

public static class ArenaRunStore
{
    public static readonly TimeSpan Ttl = TimeSpan.FromHours(1);

    /// <summary>Rounds a change gets before <see cref="ArenaRunConflictException"/>.</summary>
    public const int Attempts = 10;

    public const string DeadlinesKey = "arena_run:deadlines";

    private static readonly JsonSerializerOptions s_json = new() { Converters = { new JsonStringEnumConverter() } };

    public static string Key(string runId) => $"arena_run:{runId}";

    public static string ToJson(ArenaRun run) => JsonSerializer.Serialize(run, s_json);

    /// <summary>The run in <paramref name="json"/>; null when it is not one.</summary>
    public static ArenaRun? FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ArenaRun>(json, s_json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The run; null when there is none, or its record is not one.</summary>
    public static async Task<ArenaRun?> GetAsync(IDatabase redis, string runId) =>
        await redis.StringGetAsync(Key(runId)) is { HasValue: true } raw ? FromJson(raw.ToString()) : null;

    /// <summary>Writes a new run and its deadline; false when a run with its id exists already (nothing is written).</summary>
    public static async Task<bool> CreateAsync(IDatabase redis, ArenaRun run)
    {
        var transaction = redis.CreateTransaction();
        transaction.AddCondition(Condition.KeyNotExists(Key(run.Id)));
        Write(transaction, run);
        return await transaction.ExecuteAsync();
    }

    /// <summary>
    /// Runs <paramref name="transition"/> on the stored run (see the header) and writes it when the step was accepted. The
    /// run as changed (or as read, when refused) and the step; (null, Refused) when there is no run.
    /// </summary>
    public static async Task<(ArenaRun? Run, ArenaStep Step)> UpdateAsync(IDatabase redis, string runId, Func<ArenaRun, ArenaStep> transition)
    {
        string key = Key(runId);
        for (int attempt = 0; attempt < Attempts; attempt++)
        {
            var raw = await redis.StringGetAsync(key);
            if (!raw.HasValue || FromJson(raw.ToString()) is not { } run)
            {
                return (null, ArenaStep.Refused);
            }

            var step = transition(run);
            if (!step.Accepted)
            {
                return (run, step);
            }

            var transaction = redis.CreateTransaction();
            transaction.AddCondition(Condition.StringEqual(key, raw));
            Write(transaction, run);
            if (await transaction.ExecuteAsync())
            {
                return (run, step);
            }
        }

        throw new ArenaRunConflictException(runId);
    }

    /// <summary>The ids of the runs whose deadline is at or before <paramref name="nowMs"/>, earliest first, at most <paramref name="max"/>.</summary>
    public static async Task<IReadOnlyList<string>> DueAsync(IDatabase redis, long nowMs, int max = 100) =>
        [.. (await redis.SortedSetRangeByScoreAsync(DeadlinesKey, double.NegativeInfinity, nowMs, take: max)).Select(v => v.ToString())];

    /// <summary>Forgets a run's deadline (the loop found the run gone).</summary>
    public static Task ForgetDeadlineAsync(IDatabase redis, string runId) => redis.SortedSetRemoveAsync(DeadlinesKey, runId);

    // The record, and its deadline entry: there while the run has a deadline, gone once it has none.
    private static void Write(ITransaction transaction, ArenaRun run)
    {
        _ = transaction.StringSetAsync(Key(run.Id), ToJson(run), Ttl);
        _ = run.DeadlineMs > 0
            ? transaction.SortedSetAddAsync(DeadlinesKey, run.Id, run.DeadlineMs)
            : transaction.SortedSetRemoveAsync(DeadlinesKey, run.Id);
    }
}

/// <summary>A run change lost to other writers <see cref="ArenaRunStore.Attempts"/> times in a row.</summary>
public sealed class ArenaRunConflictException(string runId)
    : Exception($"arena_run:{runId} changed under {ArenaRunStore.Attempts} attempts in a row to change it");

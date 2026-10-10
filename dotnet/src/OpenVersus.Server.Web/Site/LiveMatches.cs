using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using OpenVersus.Server.Core.Matchmaking;
using OpenVersus.Server.Core.Ops;
using StackExchange.Redis;

namespace OpenVersus.Server.Web.Site;

/// <summary>
/// The website's live matches (GET /api/matches): one snapshot shared by every viewer, at most one sweep every 2 s as
/// the TS server's matchesCache tick made (made on request here: the web service runs no background service of its
/// own): the matches in progress (<see cref="IOpsService.MatchesAsync"/>, the same view ovsctl shows), how many
/// players are online, how many are searching in each queue (distinct players over the queue's tickets; a failed count
/// keeps the previous), whether the FFA queue is open, and when it was made.
/// </summary>
public sealed class LiveMatches(IServiceProvider services, IOpsService ops, IOptionsMonitor<FfaSettings> ffa, TimeProvider time, ILogger<LiveMatches> log)
{
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(2);
    private static readonly string[] s_queues = ["1v1", "2v2", "FFA"];
    private readonly Dictionary<string, int> _searching = s_queues.ToDictionary(q => q, _ => 0);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _online;
    private DateTimeOffset _madeAt = DateTimeOffset.MinValue;

    /// <summary>The last snapshot (empty until the first refresh).</summary>
    public JsonObject Snapshot { get; private set; } = Empty(0);

    /// <summary>The snapshot, refreshed first when the last one is <see cref="Tick"/> old (one refresh at a time; a failed one keeps the last).</summary>
    public async Task<JsonObject> SnapshotAsync(CancellationToken ct)
    {
        if (time.GetUtcNow() - _madeAt < Tick)
        {
            return Snapshot;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (time.GetUtcNow() - _madeAt >= Tick)
            {
                try
                {
                    await RefreshAsync(ct);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    log.LogError("refreshMatchesCache error: {Error}", e.Message);
                }

                _madeAt = time.GetUtcNow();
            }
        }
        finally
        {
            _gate.Release();
        }

        return Snapshot;
    }

    private static JsonObject Empty(long generatedAt) => new()
    {
        ["matches"] = new JsonArray(), ["count"] = 0, ["onlinePlayers"] = 0,
        ["searching"] = new JsonObject { ["1v1"] = 0, ["2v2"] = 0, ["FFA"] = 0 }, ["ffaOpen"] = true, ["generatedAt"] = generatedAt,
    };

    /// <summary>One refresh (the TS refreshMatchesCache).</summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        var matches = new JsonArray();
        var views = await ops.MatchesAsync();
        if (views.Value is { } list)
        {
            foreach (var m in list)
            {
                var teams = new JsonObject();
                foreach (var (team, players) in m.Teams)
                {
                    teams[team] = new JsonArray([.. players.Select(p => (JsonNode?)new JsonObject { ["playerId"] = p.Id, ["username"] = p.Name, ["character"] = p.Character })]);
                }

                matches.Add(new JsonObject
                {
                    ["setId"] = m.SetId, ["matchId"] = m.MatchId, ["mode"] = m.Mode, ["scores"] = new JsonArray([.. m.Scores.Select(s => (JsonNode?)s)]),
                    ["gamesPlayed"] = m.GamesPlayed, ["conceded"] = m.Conceded, ["teams"] = teams, ["finished"] = m.Finished,
                });
            }
        }

        var online = await ops.OnlineAsync(withPlayers: false);
        if (online.Value is { } count)
        {
            _online = count.Count;
        }

        if (services.GetService<IConnectionMultiplexer>()?.GetDatabase() is { } redis)
        {
            foreach (string queue in s_queues)
            {
                try
                {
                    var players = new HashSet<string>();
                    foreach (var raw in await redis.ListRangeAsync(queue))
                    {
                        foreach (var p in (System.Text.Json.Nodes.JsonNode.Parse(raw.ToString()) as JsonObject)?["players"] as JsonArray ?? [])
                        {
                            if (p?["id"] is JsonValue v && v.TryGetValue(out string? id))
                            {
                                players.Add(id);
                            }
                        }
                    }

                    _searching[queue] = players.Count;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    log.LogWarning("counting {Queue} searching players failed, keeping previous value: {Error}", queue, e.Message);
                }
            }
        }

        Snapshot = new JsonObject
        {
            ["matches"] = matches, ["count"] = matches.Count, ["onlinePlayers"] = _online,
            ["searching"] = new JsonObject { ["1v1"] = _searching["1v1"], ["2v2"] = _searching["2v2"], ["FFA"] = _searching["FFA"] },
            ["ffaOpen"] = FfaSchedule.IsOpen(time.GetUtcNow(), ffa.CurrentValue.WeekendOnly),
            ["generatedAt"] = time.GetUtcNow().ToUnixTimeMilliseconds(),
        };
    }
}

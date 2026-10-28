using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using OpenVersus.Server.Core.Arenas;
using OpenVersus.Server.Core.Matchmaking;
using OpenVersus.Server.Core.Ops;
using StackExchange.Redis;

namespace OpenVersus.Server.Web.Site;

/// <summary>
/// The website's live matches (GET /api/matches): one snapshot shared by every viewer, at most one sweep every 2 s as
/// the TS server's matchesCache tick made (made on request here: the web service runs no background service of its
/// own): the matches in progress (<see cref="IOpsService.MatchesAsync"/>, the same view ovsctl shows), how many
/// players are online, how many are searching in each queue (distinct players over the queue's tickets; a failed count
/// keeps the previous; "1v1 Testing Grounds" and "2v2 Testing Grounds" are their tickets' lists, testinggrounds1v1 and
/// testinggrounds2v2; "Arena" the Arena lobbies' tickets, list arena), whether the FFA and Testing Grounds queues are open
/// (testingGroundsOpen is 1v1's, testingGrounds2v2Open 2v2's; the page shows only the open ones), whether Twosday is on,
/// how many players are playing in each queue's matches (playing: the players of every match in progress and every set
/// between its games, by the mode the match is listed with; the game's main menu panel shows it beside searching), and Arenas is on
/// (arenaOpen: the game's main menu panel shows the Arena count only then; the page does not show it), and when it was made.
/// </summary>
public sealed class LiveMatches(IServiceProvider services, IOpsService ops, IOptionsMonitor<FfaSettings> ffa, TimeProvider time, ILogger<LiveMatches> log)
{
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(2);
    public const string TestingGroundsQueue = TestingGrounds.ListedMode;
    public const string TestingGrounds2v2Queue = "2v2 Testing Grounds";
    public const string ArenaQueue = "Arena";
    private static readonly string[] s_queues = ["1v1", "2v2", "FFA", TestingGroundsQueue, TestingGrounds2v2Queue, ArenaQueue];
    private static string ListOf(string queue) => queue switch
    {
        TestingGroundsQueue => TestingGrounds.List,
        TestingGrounds2v2Queue => TestingGrounds.TwoVsTwo.List,
        ArenaQueue => ArenaMatchmaking.List,
        _ => queue,
    };
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
        ["searching"] = Counts(_ => 0), ["playing"] = Counts(_ => 0),
        ["ffaOpen"] = true, ["testingGroundsOpen"] = false, ["testingGrounds2v2Open"] = false, ["twosday"] = false, ["arenaOpen"] = false,
        ["generatedAt"] = generatedAt,
    };

    // Every queue's count, in the queues' order.
    private static JsonObject Counts(Func<string, int> count) => new([.. s_queues.Select(q => KeyValuePair.Create(q, (JsonNode?)count(q)))]);

    /// <summary>One refresh (the TS refreshMatchesCache).</summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        var matches = new JsonArray();
        var playing = s_queues.ToDictionary(q => q, _ => 0);
        var views = await ops.MatchesAsync();
        if (views.Value is { } list)
        {
            foreach (var m in list)
            {
                if (!m.Finished && m.Mode is { } mode && playing.ContainsKey(mode))
                {
                    playing[mode] += m.Teams.Values.Sum(players => players.Count);
                }

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
                    foreach (var raw in await redis.ListRangeAsync(ListOf(queue)))
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

        var now = time.GetUtcNow();
        bool testingGroundsOpen = false, testingGrounds2v2Open = false, ffaOpen = false, twosday = false;
        try
        {
            twosday = await Twosday.IsActiveAsync(services, now);
            var schedule = await TestingGrounds.ScheduleAsync(services, now);
            (testingGroundsOpen, testingGrounds2v2Open) = (schedule.OneVsOne, schedule.TwoVsTwo);
            // FFA's tile gives way to 2v2 Testing Grounds while both are open (TestingGrounds.AlwaysOpen): as the menu shows them.
            ffaOpen = await FfaSchedule.IsOpenAsync(services, now, ffa.CurrentValue) && !testingGrounds2v2Open;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning("reading whether Testing Grounds and FFA are open failed: {Error}", e.Message);
        }

        Snapshot = new JsonObject
        {
            ["matches"] = matches, ["count"] = matches.Count, ["onlinePlayers"] = _online,
            ["searching"] = Counts(q => _searching[q]),
            ["playing"] = Counts(q => playing[q]),
            ["ffaOpen"] = ffaOpen,
            ["testingGroundsOpen"] = testingGroundsOpen,
            ["testingGrounds2v2Open"] = testingGrounds2v2Open,
            ["twosday"] = twosday,
            ["arenaOpen"] = ArenaHiss.IsEnabled(services),
            ["generatedAt"] = now.ToUnixTimeMilliseconds(),
        };
    }
}

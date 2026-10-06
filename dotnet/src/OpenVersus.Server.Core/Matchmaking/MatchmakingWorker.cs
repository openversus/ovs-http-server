using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Matches;
using OpenVersus.Server.Core.Realtime;
using OpenVersus.Server.Core.Settings;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Matchmaking;

// The matchmaking worker, ported from the TS server's src/matchmaking-worker.ts. Its own executable, as there
// (OpenVersus.Server.Matchmaking; never hosted in the HTTP service). On by default, Matchmaking:Enabled. Every 2 s it
// looks at the 1v1 queue, then the 2v2 queue, and makes at most one match from each. The queues are Redis lists the
// TS websocket fills (a party's ticket, when it queues) and empties (a cancel, a disconnect); this only reads them and
// takes out the tickets it matches, so it can run beside the TS worker or as several replicas at once: each queue is
// worked under a lock (matchmaking:lock:{queue}, SET NX EX 10), as the TS worker does.
//
// Per queue: drop a ticket whose player has a newer one in the queue; drop the tickets of a player whose game stopped
// answering the websocket (player_heartbeats, ms, older than 41 s; none of this while that set does not exist). Then:
//   1v1  solo tickets, oldest first; a pair matches when their average skills are within the stricter of their two
//        ranges (250 under 5 s waiting, 500 under 10 s, anything after). A 1v1 queue with fewer than 2 tickets is not
//        cleaned at all (as there).
//   2v2  from the oldest ticket, add tickets (parties whole) whose average skill is within the range of the longest
//        wait among them, until 4 players.
//   Either way, a group with a player who blocked another in it (player:{id}:blocked, a JSON list) is passed over.
//   casual1v1, casual2v2  the Casual queue (MatchmakingRequestService): as 1v1 and 2v2, with no skill range at all
//        (anyone not blocked); a player who waits too long gets bots from the game's casual_queue instead.
// A match (createMatch): match:{id} (the tickets as queued) EX 20 min; teams (parties shuffled, team 0 filled first,
// player index = place * 2 + team, a random index hosts, each player's ip from player:{id}); a map (Matchmaking/maps.json:
// an enabled one for the mode; 1v1: 1 in 999 PVE_03); p2p in the notification (Rollback:P2P and P2P.IsEligible; a P2P
// match is deployed no rollback server); ranked_set:{id} and player_ranked_set:{player} EX 20 min (every regular match:
// game 1 of a set); then the notification at {id} EX 20 min, announced (MatchLaunches: match:notifications, or
// match:launched) with matchmaking-complete for each ticket's players (MatchLauncher.MatchmakingComplete: its own
// request id), as the TS websocket built it from matchmaking:complete. A Casual match is never rated: match:{id} has isPasswordMatch (the
// TS match result skips those) and queue "casual" (for a Casual rating of its own, later), it starts no ranked set, and
// its notification is unranked (BotDefaults.UnrankedNotificationFields: isCustomGame, with bIsCustomGame set back to false
// in the game's config), so the TS websocket opens no set at its end either and no TS rating path rates it. The TS websocket does the rest (it tells the game, MIGRATION-BRIDGES.md 2).
//
// Differences from the TS worker (tools/matches/matchmaker_diff.mjs asserts them):
//   - the set's keys live 20 min (TS: 10), as long as the match's own: the TS websocket refreshes them only at the
//     game's end, and a game can take ~9.5 min from here (perks 30 s, connecting up to 45 s, up to 7.5 min of play,
//     loading and the play after the end), past which TS lost the set ("orphan match").
//   - the rollback port is IMatchLauncher's (fixed servers: a random one of theirs; on demand: the next port, deployed).
//     The TS worker always took INCR rollback:current_port, even with fixed servers, which gave a port none listens on.
//   - a matched ticket is taken out of its queue by the text it was read as; the TS worker wrote the parsed ticket out
//     again and removed that (the same text only while JSON round-trips it unchanged).
//   - a blocked pair is logged by id; the TS worker also looked their names up (a log line only).

/// <summary>Matchmaking settings.</summary>
public sealed class MatchmakingSettings
{
    [Description("This matchmaker makes matches from the queues (1v1, 2v2). Off: it keeps running and makes none. Several replicas can work at once (each queue is worked under a lock), the TS worker included, but then either may make a given match.")]
    public bool Enabled { get; set; } = true;

    [Description("Milliseconds between two looks at the queues (the TS worker's 2000).")]
    [Range(200, 60000)]
    public int IntervalMs { get; set; } = 2000;
}

internal sealed class MatchmakingWorker(IServiceProvider services, IMatchLauncher launcher, IOptionsMonitor<MatchmakingSettings> settings,
    IOptionsMonitor<RollbackSettings> rollback, TimeProvider time,
    ILogger<MatchmakingWorker> log) : BackgroundService
{
    public const string HeartbeatsKey = "player_heartbeats";
    public static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromMilliseconds(41_000);
    private static readonly TimeSpan s_lockTtl = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan s_matchTtl = TimeSpan.FromMinutes(20);
    // Until the game's end, when the TS websocket writes them again: as long as the match's own keys (see the header).
    private static readonly TimeSpan s_setTtl = TimeSpan.FromMinutes(20);
    private readonly string _workerId = $"worker_{Environment.ProcessId}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
    private bool _announced;
    private bool _warnedNoRedis;

    /// <summary>A ticket as read from its queue: the text (to take it out by), the parsed ticket, and what matching reads.</summary>
    internal sealed record Ticket(string Raw, JsonObject Json, double CreatedAt, double PartySize, IReadOnlyList<(string Id, double Skill)> Players)
    {
        public double AverageSkill => Players.Count == 0 ? 0 : Players.Sum(p => p.Skill) / Players.Count;

        public bool SharesPlayerWith(Ticket other) => other.Players.Any(p => Players.Any(q => q.Id == p.Id));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var current = settings.CurrentValue;
            if (current.Enabled && services.GetService<IConnectionMultiplexer>()?.GetDatabase() is { } redis)
            {
                if (!_announced)
                {
                    log.LogInformation("Matchmaking worker {Worker} started, looking at the queues every {Interval} ms", _workerId, current.IntervalMs);
                    _announced = true;
                }

                try
                {
                    await TickAsync(redis);
                }
                catch (Exception e) when (e is RedisException or TimeoutException or JsonException)
                {
                    log.LogError("Matchmaking tick failed: {Error}", e.Message);
                }
            }
            else if (current.Enabled && !_warnedNoRedis)
            {
                // Matchmaking is all this executable does: say why it does nothing.
                log.LogWarning("Matchmaking is on but no Redis is configured (REDIS): no matches will be made");
                _warnedNoRedis = true;
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(current.IntervalMs), time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>The Casual queue's lists (the tickets' matchType).</summary>
    public const string Casual1v1 = "casual1v1", Casual2v2 = "casual2v2";

    /// <summary>One look at every queue: at most one match from each.</summary>
    internal async Task TickAsync(IDatabase redis)
    {
        bool oneVsOne = await WithLockAsync(redis, "1v1", () => OneVsOneAsync(redis, "1v1", "1v1", skilled: true));
        bool twoVsTwo = await WithLockAsync(redis, "2v2", () => TwoVsTwoAsync(redis, "2v2", "2v2", skilled: true));
        bool casual1 = await WithLockAsync(redis, Casual1v1, () => OneVsOneAsync(redis, Casual1v1, "1v1", skilled: false));
        bool casual2 = await WithLockAsync(redis, Casual2v2, () => TwoVsTwoAsync(redis, Casual2v2, "2v2", skilled: false));
        if (oneVsOne || twoVsTwo || casual1 || casual2)
        {
            log.LogInformation("Matches made this tick: 1v1={OneVsOne} 2v2={TwoVsTwo} casual1v1={Casual1} casual2v2={Casual2}", oneVsOne, twoVsTwo, casual1, casual2);
        }
    }

    private async Task<bool> WithLockAsync(IDatabase redis, string queue, Func<Task<bool>> work)
    {
        string key = $"matchmaking:lock:{queue}";
        if (!await redis.StringSetAsync(key, _workerId, s_lockTtl, When.NotExists))
        {
            return false;
        }

        try
        {
            return await work();
        }
        finally
        {
            await redis.LockReleaseAsync(key, _workerId);
        }
    }

    // ── 1v1 ─────────────────────────────────────────────────────────────────────────────────────────────────────────
    private async Task<bool> OneVsOneAsync(IDatabase redis, string queue, string mode, bool skilled)
    {
        var tickets = await TicketsAsync(redis, queue);
        if (tickets.Count < 2)
        {
            return false;
        }

        tickets = await DropDuplicatesAsync(redis, queue, tickets);
        tickets = await DropSilentAsync(redis, queue, tickets);
        var solo = tickets.Where(t => t.PartySize == 1).OrderBy(t => t.CreatedAt).ToList();
        if (solo.Count < 2)
        {
            return false;
        }

        double now = Math.Floor(time.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0);
        for (int i = 0; i < solo.Count; i++)
        {
            for (int j = i + 1; j < solo.Count; j++)
            {
                var (a, b) = (solo[i], solo[j]);
                if (a.SharesPlayerWith(b))
                {
                    continue;
                }

                // Both must be in range: the stricter (shorter wait) range of the two.
                double range = skilled ? Math.Min(SkillRange(now - a.CreatedAt), SkillRange(now - b.CreatedAt)) : double.PositiveInfinity;
                if (Math.Abs(a.AverageSkill - b.AverageSkill) > range)
                {
                    continue;
                }

                if (await BlockedPairAsync(redis, [a, b]) is { } blocked)
                {
                    log.LogWarning("Not matching {First} and {Second} in {Queue}: {Blocker} blocked {Blocked}", Str(a.Json, "matchmakingRequestId"), Str(b.Json, "matchmakingRequestId"), queue, blocked.Blocker, blocked.Blocked);
                    continue;
                }

                await RemoveAsync(redis, queue, [a, b]);
                await CreateMatchAsync(redis, [a, b], mode, queue);
                log.LogInformation("Skill matched in {Queue}: {First} vs {Second}", queue, a.AverageSkill, b.AverageSkill);
                return true;
            }
        }

        return false;
    }

    // ── 2v2 ─────────────────────────────────────────────────────────────────────────────────────────────────────────
    private async Task<bool> TwoVsTwoAsync(IDatabase redis, string queue, string mode, bool skilled)
    {
        var tickets = await TicketsAsync(redis, queue);
        tickets = await DropDuplicatesAsync(redis, queue, tickets);
        tickets = await DropSilentAsync(redis, queue, tickets);
        if (tickets.Sum(t => t.Players.Count) < 4)
        {
            return false;
        }

        double now = Math.Floor(time.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0);
        var sorted = tickets.OrderBy(t => t.CreatedAt).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            var candidates = new List<Ticket> { sorted[i] };
            int players = sorted[i].Players.Count;
            for (int j = i + 1; j < sorted.Count && players < 4; j++)
            {
                if (sorted[i].SharesPlayerWith(sorted[j]))
                {
                    continue;
                }

                // The candidates' average against this ticket's, within the range of the longest wait among them all.
                double existing = candidates.Average(t => t.AverageSkill);
                double longest = Math.Max(candidates.Max(t => now - t.CreatedAt), now - sorted[j].CreatedAt);
                if ((!skilled || Math.Abs(existing - sorted[j].AverageSkill) <= SkillRange(longest)) && players + sorted[j].Players.Count <= 4)
                {
                    candidates.Add(sorted[j]);
                    players += sorted[j].Players.Count;
                }
            }

            if (players != 4)
            {
                continue;
            }

            if (await BlockedPairAsync(redis, candidates) is { } blocked)
            {
                log.LogWarning("Not matching {Requests} in {Queue}: {Blocker} blocked {Blocked}", string.Join(", ", candidates.Select(c => Str(c.Json, "matchmakingRequestId"))), queue, blocked.Blocker, blocked.Blocked);
                continue;
            }

            log.LogInformation("Skill matched in {Queue} ({Composition}): skills [{Skills}]", queue, string.Join("+", candidates.Select(c => c.Players.Count)),
                string.Join(", ", candidates.Select(c => Math.Round(c.AverageSkill))));
            await RemoveAsync(redis, queue, candidates);
            await CreateMatchAsync(redis, candidates, mode, queue);
            return true;
        }

        return false;
    }

    /// <summary>The range two skills may be apart after waiting <paramref name="seconds"/>: 250, 500, then anything.</summary>
    internal static double SkillRange(double seconds) => seconds < 5 ? 250 : seconds < 10 ? 500 : double.PositiveInfinity;

    // ── The queue ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The queue's tickets, in its order; one that does not parse is skipped (and stays).</summary>
    internal async Task<List<Ticket>> TicketsAsync(IDatabase redis, string queue)
    {
        var tickets = new List<Ticket>();
        foreach (var raw in await redis.ListRangeAsync(queue))
        {
            string text = raw.ToString();
            try
            {
                if (JsonNode.Parse(text) is JsonObject json)
                {
                    var players = (json["players"] as JsonArray ?? []).OfType<JsonObject>()
                        .Select(p => (Str(p, "id") ?? "", Number(p["skill"]))).ToList();
                    tickets.Add(new Ticket(text, json, Number(json["created_at"]), Number(json["party_size"]), players));
                }
            }
            catch (JsonException)
            {
                log.LogWarning("Skipping a ticket in {Queue} that is not JSON: {Ticket}", queue, text);
            }
        }

        return tickets;
    }

    /// <summary>A player with two tickets keeps the newer (the later created_at; the later in the queue on a tie).</summary>
    private async Task<List<Ticket>> DropDuplicatesAsync(IDatabase redis, string queue, List<Ticket> tickets)
    {
        var seen = new Dictionary<string, int>();
        var stale = new HashSet<int>();
        for (int i = 0; i < tickets.Count; i++)
        {
            foreach (var (id, _) in tickets[i].Players)
            {
                if (seen.TryGetValue(id, out int previous))
                {
                    if (tickets[i].CreatedAt >= tickets[previous].CreatedAt)
                    {
                        stale.Add(previous);
                        seen[id] = i;
                    }
                    else
                    {
                        stale.Add(i);
                    }
                }
                else
                {
                    seen[id] = i;
                }
            }
        }

        if (stale.Count == 0)
        {
            return tickets;
        }

        var removed = stale.Select(i => tickets[i]).ToList();
        log.LogWarning("Removing {Count} duplicate ticket(s) from {Queue} for players: {Players}", removed.Count, queue, string.Join(", ", removed.SelectMany(t => t.Players.Select(p => p.Id))));
        await RemoveAsync(redis, queue, removed);
        return [.. tickets.Where((_, i) => !stale.Contains(i))];
    }

    /// <summary>The tickets of a player whose game stopped answering (no heartbeat in 41 s, or none) go.</summary>
    private async Task<List<Ticket>> DropSilentAsync(IDatabase redis, string queue, List<Ticket> tickets)
    {
        if (!await redis.KeyExistsAsync(HeartbeatsKey))
        {
            return tickets;
        }

        var ids = tickets.SelectMany(t => t.Players.Select(p => p.Id)).Distinct().ToList();
        var lastSeen = new Dictionary<string, double>();
        foreach (var id in ids)
        {
            if (await redis.SortedSetScoreAsync(HeartbeatsKey, id) is { } score)
            {
                lastSeen[id] = score;
            }
        }

        double cutoff = time.GetUtcNow().ToUnixTimeMilliseconds() - HeartbeatTimeout.TotalMilliseconds;
        bool Silent(string id) => !lastSeen.TryGetValue(id, out double at) || at < cutoff;
        var silent = tickets.Where(t => t.Players.Any(p => Silent(p.Id))).ToList();
        if (silent.Count == 0)
        {
            return tickets;
        }

        log.LogWarning("Removing {Count} ticket(s) from {Queue} whose players stopped answering: {Players}", silent.Count, queue,
            string.Join(", ", silent.SelectMany(t => t.Players.Where(p => Silent(p.Id)).Select(p => p.Id))));
        await RemoveAsync(redis, queue, silent);
        return [.. tickets.Except(silent)];
    }

    /// <summary>Takes the tickets out of the queue, in one transaction, by the text they were read as.</summary>
    private static async Task RemoveAsync(IDatabase redis, string queue, IReadOnlyList<Ticket> tickets)
    {
        var transaction = redis.CreateTransaction();
        var removals = tickets.Select(t => transaction.ListRemoveAsync(queue, t.Raw)).ToList();
        await transaction.ExecuteAsync();
        await Task.WhenAll(removals);
    }

    /// <summary>The first block between two players of the group (each player's list, in the group's order), or null.</summary>
    private static async Task<(string Blocker, string Blocked)?> BlockedPairAsync(IDatabase redis, IReadOnlyList<Ticket> group)
    {
        var ids = group.SelectMany(t => t.Players.Select(p => p.Id)).ToList();
        foreach (var id in ids)
        {
            var raw = await redis.StringGetAsync($"player:{id}:blocked");
            if (raw.IsNullOrEmpty || JsonNode.Parse(raw.ToString()) is not JsonArray blocked)
            {
                continue;
            }

            foreach (var other in blocked)
            {
                if (other is JsonValue v && v.TryGetValue<string>(out var b) && ids.Contains(b))
                {
                    return (id, b);
                }
            }
        }

        return null;
    }

    // ── The match ───────────────────────────────────────────────────────────────────────────────────────────────────

    private async Task CreateMatchAsync(IDatabase redis, List<Ticket> tickets, string mode, string queue)
    {
        bool casual = queue is Casual1v1 or Casual2v2;
        int total = tickets.Sum(t => t.Players.Count);
        string matchId = ObjectId.GenerateNewId().ToString();
        string resultId = ObjectId.GenerateNewId().ToString();
        if (await launcher.RollbackPortAsync(redis) is not { } port)
        {
            log.LogError("No rollback port for a {Mode} match: the tickets are out of the queue and no match was made", mode);
            return;
        }

        var match = new JsonObject
        {
            ["matchId"] = matchId,
            ["resultId"] = resultId,
            ["tickets"] = new JsonArray([.. tickets.Select(t => (JsonNode)t.Json.DeepClone())]),
            ["status"] = "pending",
            ["createdAt"] = time.GetUtcNow().ToUnixTimeMilliseconds(),
            ["matchType"] = queue,
            ["totalPlayers"] = total,
            ["rollbackPort"] = port,
        };
        if (casual)
        {
            // Never rated (the TS match result skips a password match); the queue, for a Casual rating of its own later.
            match["isPasswordMatch"] = true;
            match["queue"] = "casual";
        }

        await redis.StringSetAsync($"match:{matchId}", Js.Stringify(match), s_matchTtl);

        var players = await TeamsAsync(redis, tickets);
        var notification = new JsonObject
        {
            ["players"] = players,
            ["matchId"] = matchId,
            ["matchKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            ["map"] = MatchmakingMaps.Pick(mode, matchId, log),
            ["mode"] = mode,
            ["rollbackPort"] = port,
        };
        if (casual)
        {
            // Unranked for the TS websocket and match end (no set, no ratings, the unranked config).
            foreach (var (key, value) in Matches.BotDefaults.UnrankedNotificationFields())
            {
                notification[key] = value?.DeepClone();
            }

            notification["gameplayConfigOverride"] = Matches.BotDefaults.UnrankedConfigOverride();
        }

        // As the TS worker's markP2P: a P2P match gets no rollback server unless its nodes find no direct path.
        bool p2p = Matches.P2P.Mark(notification, rollback.CurrentValue.P2P);
        if (!p2p)
        {
            launcher.DeployIfOnDemand(port, matchId);
        }

        // Every regular match is game 1 of a set: its state from the start, so a disconnect in game 1 is already a ranked
        // one. A Casual match has no set: sets are what the TS server rates.
        var ids = tickets.SelectMany(t => t.Players.Select(p => p.Id)).ToList();
        if (!casual)
        {
            await redis.StringSetAsync($"ranked_set:{matchId}", Js.Stringify(new JsonObject
            {
                ["players"] = players.DeepClone(),
                ["mode"] = mode,
                ["gamesPlayed"] = 0,
                ["scores"] = new JsonArray(0, 0),
                ["checkins"] = new JsonArray(),
            }), s_setTtl);
            foreach (var id in ids)
            {
                await redis.StringSetAsync($"player_ranked_set:{id}", matchId, s_setTtl);
            }
        }

        // Announced once its set is written (a match that cannot be told ends its set: MatchLaunches), with one
        // matchmaking-complete per ticket: each party's own request.
        await MatchLaunches.AnnounceAsync(services, redis, matchId, Js.Stringify(notification),
            [.. tickets.Select(t => new MatchComplete([.. t.Players.Select(p => p.Id)], t.Json["matchmakingRequestId"], Searching: true))]);

        log.LogInformation("Created {Mode} match {Match} from {Queue} with {Players} players across {Tickets} tickets on rollback port {Port}{P2P}", mode, matchId, queue, total, tickets.Count, port,
            p2p ? " (P2P: the players connect to their own nodes; a relay only if no direct path opens)" : "");
    }

    /// <summary>
    /// The players' entries (createTeams): parties shuffled, each whole on team 0 while it fits in half the players,
    /// else team 1; index = place in team * 2 + team; the player at a random index hosts; ip from player:{id} (left out
    /// when it has none). No isBot: there are no bots here.
    /// </summary>
    internal static async Task<JsonArray> TeamsAsync(IDatabase redis, IReadOnlyList<Ticket> tickets)
    {
        int total = tickets.Sum(t => t.Players.Count);
        double perTeam = total / 2.0;
        var shuffled = tickets.ToArray();
        for (int i = shuffled.Length - 1; i > 0; i--)
        {
            int j = Random.Shared.Next(i + 1);
            (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
        }

        var teamOf = new Dictionary<Ticket, int>();
        int used0 = 0;
        foreach (var party in shuffled)
        {
            if (used0 + party.Players.Count <= perTeam)
            {
                teamOf[party] = 0;
                used0 += party.Players.Count;
            }
            else
            {
                teamOf[party] = 1;
            }
        }

        int host = Random.Shared.Next(total);
        var entries = new JsonArray();
        foreach (int team in new[] { 0, 1 })
        {
            int place = 0;
            foreach (var party in shuffled.Where(p => teamOf[p] == team))
            {
                foreach (var (id, _) in party.Players)
                {
                    int index = place * 2 + team;
                    var entry = new JsonObject { ["playerId"] = id };
                    if (party.Json["partyId"] is { } partyId)
                    {
                        entry["partyId"] = partyId.DeepClone();
                    }

                    entry["playerIndex"] = index;
                    entry["teamIndex"] = team;
                    entry["isHost"] = index == host;
                    if (await redis.HashGetAsync($"player:{id}", "ip") is { HasValue: true } ip)
                    {
                        entry["ip"] = ip.ToString();
                    }

                    entries.Add(entry);
                    place++;
                }
            }
        }

        return entries;
    }

    private static string? Str(JsonObject? obj, string key) => obj?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    // A number as JavaScript would add it (a missing one is NaN there: never in range).
    private static double Number(JsonNode? node) => node is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? v.GetValue<double>() : double.NaN;
}

/// <summary>The maps a match is played on, and which have hazards (Matchmaking/maps.json, from the TS server's data/maps1v1.json and maps2v2.json).</summary>
internal static class MatchmakingMaps
{
    private static readonly Lazy<JsonObject> s_maps = new(() =>
    {
        using var stream = typeof(MatchmakingMaps).Assembly.GetManifestResourceStream("OpenVersus.Server.Core.Matchmaking.maps.json")
            ?? throw new InvalidOperationException("maps.json is not embedded");
        return (JsonObject)JsonNode.Parse(stream)!;
    });

    /// <summary>
    /// An enabled map for the mode (2v2's for 2v2, 1v1's for anything else); for 1v1, 1 time in 999 PVE_03. None
    /// enabled: one of the mode's fallback list.
    /// </summary>
    public static string Pick(string mode, string matchId, ILogger log)
    {
        string list = mode == "2v2" ? "2v2" : "1v1";
        // The TS randomInt(1, 1000) == 69.
        if (mode == "1v1" && Random.Shared.Next(1, 1000) == 69)
        {
            log.LogInformation("The 1 in 999 map for match {Match}: PVE_03", matchId);
            return "PVE_03";
        }

        var enabled = (s_maps.Value[list] as JsonArray ?? []).Where(m => m?["enabled"]?.GetValue<bool>() == true).Select(m => m!["id"]!.GetValue<string>()).ToList();
        if (enabled.Count > 0)
        {
            return enabled[Random.Shared.Next(enabled.Count)];
        }

        var fallback = (s_maps.Value["fallback"]?[list] as JsonArray ?? []).Select(m => m!.GetValue<string>()).ToList();
        log.LogError("No enabled {List} maps: match {Match} gets one from the fallback list", list, matchId);
        return fallback[Random.Shared.Next(fallback.Count)];
    }

    /// <summary>
    /// Whether <paramref name="map"/> has hazards, as the TS server's getMapHazards (data/maps.ts): PVE_03 always; else
    /// its entry in the mode's list (2v2's for 2v2, 1v1's for anything else), the id compared without case; false when
    /// it has none.
    /// </summary>
    public static bool Hazards(string map, string? mode)
    {
        if (map == "PVE_03")
        {
            return true;
        }

        string list = mode == "2v2" ? "2v2" : "1v1";
        var entry = (s_maps.Value[list] as JsonArray ?? []).FirstOrDefault(m => string.Equals(m?["id"]?.GetValue<string>(), map, StringComparison.OrdinalIgnoreCase));
        return entry?["hazards"] is JsonValue hazards && hazards.GetValueKind() == JsonValueKind.True;
    }
}

public static class MatchmakingHosting
{
    public static WebApplicationBuilder AddMatchmaking(this WebApplicationBuilder builder)
    {
        builder.AddSetting<MatchmakingSettings>("Matchmaking");
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddHostedService<MatchmakingWorker>();
        return builder;
    }
}

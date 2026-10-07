using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Control;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Ops;

// Data the TS server owns, read and written here exactly as it does. Every name below is migration contract: the
// C# services that replace the TS ones must keep writing them the same way, or this (and the website) breaks.
//
// Redis
//   1v1, 2v2                     lists of matchmaking tickets, JSON: { party_size, players: [{ id, skill, region }],
//                                created_at (unix seconds), partyId, matchmakingRequestId, ... } (the queues the
//                                matchmaking worker processes)
//   online_players               set of connected player ids
//   connections:{playerId}       hash; username (the display name), hydraUsername (the generated fallback), character,
//                                steamId, current_ip (the online list; finding an online player by IP address)
//   player:{playerId}            hash; status (idle, queued, in_match, ...)
//   match_started:{matchId}      set by the rollback server while a game is being played (10 min TTL)
//   {matchId}                    the match's config, JSON: { players: [{ playerId, teamIndex, isSpectator }], mode,
//                                isCustomGame } (20 min TTL)
//   player_ranked_set:{playerId} the ranked set a player's game belongs to
//   ranked_set:{setId}           the set's state, JSON: { players, mode, scores, gamesPlayed, conceded }
//   match_characters:{setId}     JSON { playerId: character }
//   ws:disconnect                published { playerId }: the realtime gateway node holding that player's connection drops it
// Mongo
//   playertesters                one document per player: _id (ObjectId; its hex is the player id above), name,
//                                hydraUsername, steamId, public_id, profile_id

/// <summary>A queued player, as ovsctl shows it.</summary>
public sealed record QueuedPlayer(string Id, string Name, double? Skill);

/// <summary>A matchmaking ticket: one party waiting in a queue.</summary>
public sealed record QueuedTicket(string PartyId, string MatchmakingRequestId, long WaitingSeconds, IReadOnlyList<QueuedPlayer> Players);

/// <summary>One queue: how many parties and players are in it, and who.</summary>
public sealed record QueueView(string Queue, int Tickets, int Players, IReadOnlyList<QueuedTicket> Entries);

/// <summary>A connected player.</summary>
/// <summary>A connected player: who, and the handles the player commands take (username, Steam id, the IP connected from).</summary>
public sealed record OnlinePlayer(string Id, string Name, string? Status, string? Username = null, string? SteamId = null, string? Ip = null);

/// <summary>How many players are connected, and (when asked) who.</summary>
public sealed record OnlineView(long Count, IReadOnlyList<OnlinePlayer>? Players);

/// <summary>A player in a match.</summary>
public sealed record MatchPlayer(string Id, string Name, string Character);

/// <summary>A match (a ranked set, or a single game) in progress, as the website's /matches shows it.</summary>
public sealed record MatchView(string SetId, string MatchId, string? Mode, IReadOnlyList<int> Scores, int GamesPlayed, bool Conceded, IReadOnlyDictionary<string, IReadOnlyList<MatchPlayer>> Teams);

/// <summary>A player's record.</summary>
public sealed record PlayerView(string Id, string Name, string? HydraUsername, string? SteamId, string? PublicId, string? ProfileId, bool Online, string? Status);

/// <summary>A forced disconnect sent: to whom, whether they were online, and how many websocket services heard it.</summary>
public sealed record DisconnectView(string Id, string Name, bool WasOnline, long Websockets);

/// <summary>
/// Operations on the live game's state for administrators: queues, connected players, matches in progress, and player
/// records. Behind the control API and its access policy, like the settings.
/// </summary>
public interface IOpsService
{
    Task<ControlResult<IReadOnlyList<QueueView>>> QueuesAsync();

    Task<ControlResult<OnlineView>> OnlineAsync(bool withPlayers);

    Task<ControlResult<IReadOnlyList<MatchView>>> MatchesAsync();

    /// <summary>A player by id (ObjectId hex), else by exact name (any case), else by generated username, else by Steam
    /// id, else by the IP address an online player is connected from.</summary>
    Task<ControlResult<PlayerView>> FindPlayerAsync(string who);

    /// <summary>Renames a player; see the implementation for how this differs from the website's name change.</summary>
    Task<ControlResult<PlayerView>> RenamePlayerAsync(string who, string newName);

    /// <summary>Closes the player's game websocket, as a heartbeat timeout would (the client logs out).</summary>
    Task<ControlResult<DisconnectView>> DisconnectPlayerAsync(string who);
}

internal sealed class OpsService : IOpsService
{
    public static readonly string[] Queues = ["1v1", "2v2"];
    private const int MaxNameLength = 24;
    private const string PlayersCollection = "playertesters";
    private readonly IServiceProvider _services;
    private readonly ILogger<OpsService> _log;

    public OpsService(IServiceProvider services, ILogger<OpsService> log)
    {
        _services = services;
        _log = log;
    }

    // Resolved per call rather than injected: a service without Redis or Mongo still starts, and only the operations
    // that need the missing one are refused.
    private IDatabase? Redis => _services.GetService<IConnectionMultiplexer>()?.GetDatabase();

    private IMongoCollection<BsonDocument>? Players => _services.GetService<IMongoDatabase>()?.GetCollection<BsonDocument>(PlayersCollection);

    private static ControlResult<T> NoRedis<T>() => ControlResult<T>.Refused("this service has no Redis configured (REDIS)");

    private static ControlResult<T> NoMongo<T>() => ControlResult<T>.Refused("this service has no Mongo configured (MONGODB_URI)");

    public async Task<ControlResult<IReadOnlyList<QueueView>>> QueuesAsync()
    {
        if (Redis is not { } redis)
        {
            return NoRedis<IReadOnlyList<QueueView>>();
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var views = new List<QueueView>();
        foreach (string queue in Queues)
        {
            var entries = new List<QueuedTicket>();
            foreach (var raw in await redis.ListRangeAsync(queue))
            {
                using var ticket = JsonDocument.Parse(raw.ToString());
                var root = ticket.RootElement;
                var players = new List<QueuedPlayer>();
                if (root.TryGetProperty("players", out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (var p in list.EnumerateArray())
                    {
                        string id = Str(p, "id") ?? "?";
                        players.Add(new QueuedPlayer(id, await DisplayNameAsync(redis, id), p.TryGetProperty("skill", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetDouble() : null));
                    }
                }

                long created = root.TryGetProperty("created_at", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt64() : now;
                entries.Add(new QueuedTicket(Str(root, "partyId") ?? "?", Str(root, "matchmakingRequestId") ?? "?", Math.Max(0, now - created), players));
            }

            views.Add(new QueueView(queue, entries.Count, entries.Sum(e => e.Players.Count), entries));
        }

        return ControlResult<IReadOnlyList<QueueView>>.Ok(views);
    }

    public async Task<ControlResult<OnlineView>> OnlineAsync(bool withPlayers)
    {
        if (Redis is not { } redis)
        {
            return NoRedis<OnlineView>();
        }

        long count = await redis.SetLengthAsync("online_players");
        if (!withPlayers)
        {
            return ControlResult<OnlineView>.Ok(new OnlineView(count, null));
        }

        var players = new List<OnlinePlayer>();
        foreach (var member in await redis.SetMembersAsync("online_players"))
        {
            string id = member.ToString();
            var handles = await redis.HashGetAsync($"connections:{id}", ["hydraUsername", "steamId", "current_ip"]);
            players.Add(new OnlinePlayer(id, await DisplayNameAsync(redis, id), (string?)await redis.HashGetAsync($"player:{id}", "status"),
                NullIfEmpty(handles[0]), NullIfEmpty(handles[1]), NullIfEmpty(handles[2])));
        }

        return ControlResult<OnlineView>.Ok(new OnlineView(count, players.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList()));
    }

    // The website's refreshMatchesCache (server.ts), both passes: games being played (match_started:*), then ranked
    // sets between games (ranked_set:*), merged by set id; custom games are left out, as there.
    public async Task<ControlResult<IReadOnlyList<MatchView>>> MatchesAsync()
    {
        if (Redis is not { } redis)
        {
            return NoRedis<IReadOnlyList<MatchView>>();
        }

        var results = new List<MatchView>();
        var seen = new HashSet<string>();
        foreach (string key in await KeysAsync("match_started:*"))
        {
            string matchId = key["match_started:".Length..];
            using var config = await JsonAsync(redis, matchId);
            if (config is null || !config.RootElement.TryGetProperty("players", out var players) || players.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            if (config.RootElement.TryGetProperty("isCustomGame", out var custom) && custom.ValueKind == JsonValueKind.True)
            {
                continue;
            }

            string setId = matchId;
            string? first = players.EnumerateArray().Select(p => Str(p, "playerId")).FirstOrDefault(id => id is not null);
            if (first is not null && (string?)await redis.StringGetAsync($"player_ranked_set:{first}") is { } mapped)
            {
                setId = mapped;
            }

            if (!seen.Add(setId))
            {
                continue;
            }

            using var set = await JsonAsync(redis, $"ranked_set:{setId}");
            results.Add(await MatchAsync(redis, setId, matchId, Str(config.RootElement, "mode"), players, set?.RootElement));
        }

        foreach (string key in await KeysAsync("ranked_set:*"))
        {
            string setId = key["ranked_set:".Length..];
            if (seen.Contains(setId))
            {
                continue;
            }

            using var set = await JsonAsync(redis, key);
            if (set is null || !set.RootElement.TryGetProperty("players", out var players) || players.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            seen.Add(setId);
            results.Add(await MatchAsync(redis, setId, setId, Str(set.RootElement, "mode"), players, set.RootElement));
        }

        return ControlResult<IReadOnlyList<MatchView>>.Ok(results);
    }

    public async Task<ControlResult<PlayerView>> FindPlayerAsync(string who)
    {
        if (Players is not { } players)
        {
            return NoMongo<PlayerView>();
        }

        var found = await ResolveAsync(players, who);
        return found.Error is not null ? ControlResult<PlayerView>.Missing(found.Error) : ControlResult<PlayerView>.Ok(await ViewAsync(found.Value!));
    }

    // Like the website's /namechange (server.ts): not blank, cut to 24 characters, unique regardless of case; Mongo's
    // name first, then the live connection hash so the new name shows at once. Deliberately different, because this is
    // an administrator's rename: no banned-name check and no censoring (renaming someone out of an offensive name is
    // the point), no IP ban, no per-IP legacy mirror (there is no request IP here), and the connection hash is only
    // written if it exists (the website's write creates a partial one for a player who is not connected). The
    // uniqueness check is read-then-write here as there, so two renames to one name at the same moment can both pass.
    public async Task<ControlResult<PlayerView>> RenamePlayerAsync(string who, string newName)
    {
        if (Players is not { } players)
        {
            return NoMongo<PlayerView>();
        }

        string name = newName.Trim();
        if (name.Length > MaxNameLength)
        {
            name = name[..MaxNameLength].Trim();
        }

        if (name.Length == 0)
        {
            return ControlResult<PlayerView>.Refused("a blank name is not permitted");
        }

        var found = await ResolveAsync(players, who);
        if (found.Error is not null)
        {
            return ControlResult<PlayerView>.Missing(found.Error);
        }

        var player = found.Value!;
        var id = player["_id"].AsObjectId;
        var sameName = Builders<BsonDocument>.Filter.Regex("name", new BsonRegularExpression($"^{Regex.Escape(name)}$", "i"));
        var taken = await players.Find(sameName & Builders<BsonDocument>.Filter.Ne("_id", id)).Limit(1).FirstOrDefaultAsync();
        if (taken is not null)
        {
            return ControlResult<PlayerView>.Refused($"the name \"{name}\" is already taken by player {taken["_id"].AsObjectId}");
        }

        await players.UpdateOneAsync(Builders<BsonDocument>.Filter.Eq("_id", id), Builders<BsonDocument>.Update.Set("name", name));
        if (Redis is { } redis && await redis.KeyExistsAsync($"connections:{id}"))
        {
            await redis.HashSetAsync($"connections:{id}", "username", name);
        }

        _log.LogInformation("Renamed player {Id} from \"{Old}\" to \"{New}\"", id, Str(player, "name"), name);
        return await FindPlayerAsync(id.ToString());
    }

    // The realtime gateway node holding the player's connection drops it when it hears ws:disconnect {playerId} (no code:
    // at once, as the TS websocket's terminate()), so its close runs the usual cleanup (ticket, lobby, session) and the
    // client logs out.
    public const string DisconnectChannel = "ws:disconnect";

    public async Task<ControlResult<DisconnectView>> DisconnectPlayerAsync(string who)
    {
        if (Players is not { } players)
        {
            return NoMongo<DisconnectView>();
        }

        if (Redis is not { } redis)
        {
            return NoRedis<DisconnectView>();
        }

        var found = await ResolveAsync(players, who);
        if (found.Error is not null)
        {
            return ControlResult<DisconnectView>.Missing(found.Error);
        }

        var view = await ViewAsync(found.Value!);
        long heard = await redis.PublishAsync(RedisChannel.Literal(DisconnectChannel), JsonSerializer.Serialize(new { playerId = view.Id }));
        if (heard == 0)
        {
            return ControlResult<DisconnectView>.Refused($"no websocket service is listening on {DisconnectChannel} (not running, or older than this command)");
        }

        _log.LogInformation("Asked {Heard} websocket service(s) to disconnect player {Id} (\"{Name}\", online: {Online})", heard, view.Id, view.Name, view.Online);
        return ControlResult<DisconnectView>.Ok(new DisconnectView(view.Id, view.Name, view.Online, heard));
    }

    private async Task<(BsonDocument? Value, string? Error)> ResolveAsync(IMongoCollection<BsonDocument> players, string who)
    {
        if (ObjectId.TryParse(who, out var id))
        {
            var byId = await players.Find(Builders<BsonDocument>.Filter.Eq("_id", id)).FirstOrDefaultAsync();
            if (byId is not null)
            {
                return (byId, null);
            }
        }

        var byName = await players.Find(Builders<BsonDocument>.Filter.Regex("name", new BsonRegularExpression($"^{Regex.Escape(who)}$", "i"))).Limit(3).ToListAsync();
        if (byName.Count > 1)
        {
            return (null, $"more than one player is named \"{who}\"; use the player id ({string.Join(", ", byName.Select(p => p["_id"].AsObjectId))})");
        }

        if (byName.Count == 1)
        {
            return (byName[0], null);
        }

        var byUsername = await players.Find(Builders<BsonDocument>.Filter.Eq("hydraUsername", who)).Limit(2).ToListAsync();
        if (byUsername.Count == 1)
        {
            return (byUsername[0], null);
        }

        var bySteam = await players.Find(Builders<BsonDocument>.Filter.Eq("steamId", who)).Limit(2).ToListAsync();
        if (bySteam.Count > 1)
        {
            return (null, $"more than one player has Steam id {who}; use the player id");
        }

        if (bySteam.Count == 1)
        {
            return (bySteam[0], null);
        }

        // Last, an IP address: the online players connected from it (connections:{id} current_ip). Several players can
        // share one (a household, a proxy), so more than one is refused with their ids.
        if (System.Net.IPAddress.TryParse(who, out var wanted) && Redis is { } redis)
        {
            var online = await redis.SetMembersAsync("online_players");
            var fromIp = new List<string>();
            foreach (var member in online)
            {
                // Compared as addresses: an IPv4 address and its IPv6-mapped form (::ffff:a.b.c.d) are one.
                if (System.Net.IPAddress.TryParse((string?)await redis.HashGetAsync($"connections:{member}", "current_ip"), out var ip)
                    && ip.MapToIPv6().Equals(wanted.MapToIPv6()))
                {
                    fromIp.Add(member.ToString());
                }
            }

            if (fromIp.Count > 1)
            {
                return (null, $"more than one online player is connected from {who}; use the player id ({string.Join(", ", fromIp)})");
            }

            if (fromIp.Count == 1 && ObjectId.TryParse(fromIp[0], out var ipId)
                && await players.Find(Builders<BsonDocument>.Filter.Eq("_id", ipId)).FirstOrDefaultAsync() is { } byIp)
            {
                return (byIp, null);
            }
        }

        return (null, $"no player with id, name, username, Steam id or (online) IP address \"{who}\"");
    }

    private async Task<PlayerView> ViewAsync(BsonDocument player)
    {
        string id = player["_id"].AsObjectId.ToString();
        bool online = false;
        string? status = null;
        if (Redis is { } redis)
        {
            online = await redis.SetContainsAsync("online_players", id);
            status = await redis.HashGetAsync($"player:{id}", "status");
        }

        return new PlayerView(id, Str(player, "name") ?? "", Str(player, "hydraUsername"), Str(player, "steamId"), Str(player, "public_id"),
            player.TryGetValue("profile_id", out var profile) && !profile.IsBsonNull ? profile.ToString() : null, online, status);
    }

    private async Task<MatchView> MatchAsync(IDatabase redis, string setId, string matchId, string? mode, JsonElement players, JsonElement? set)
    {
        using var characters = await JsonAsync(redis, $"match_characters:{setId}");
        var teams = new Dictionary<string, List<MatchPlayer>>();
        foreach (var p in players.EnumerateArray())
        {
            string id = Str(p, "playerId") ?? "?";
            string team = p.TryGetProperty("isSpectator", out var spectator) && spectator.ValueKind == JsonValueKind.True ? "4"
                : p.TryGetProperty("teamIndex", out var index) && index.ValueKind == JsonValueKind.Number ? index.GetInt32().ToString() : "0";
            string character = (await redis.HashGetAsync($"connections:{id}", "character")).ToString() is { Length: > 0 } c ? c
                : characters is not null && characters.RootElement.TryGetProperty(id, out var mc) && mc.ValueKind == JsonValueKind.String ? mc.GetString()! : "unknown";
            if (!teams.TryGetValue(team, out var members))
            {
                teams[team] = members = [];
            }

            members.Add(new MatchPlayer(id, await DisplayNameAsync(redis, id), character));
        }

        var scores = new List<int> { 0, 0 };
        if (set is { } s && s.TryGetProperty("scores", out var sc) && sc.ValueKind == JsonValueKind.Array)
        {
            scores = sc.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.Number).Select(v => v.GetInt32()).ToList();
        }

        int games = set is { } g && g.TryGetProperty("gamesPlayed", out var gp) && gp.ValueKind == JsonValueKind.Number ? gp.GetInt32() : 0;
        bool conceded = set is { } cs && cs.TryGetProperty("conceded", out var cv) && cv.ValueKind == JsonValueKind.True;
        return new MatchView(setId, matchId, mode, scores, games, conceded, teams.ToDictionary(t => t.Key, t => (IReadOnlyList<MatchPlayer>)t.Value));
    }

    // The website's fallback chain: the display name, else the generated Hydra name, else "Unknown".
    private static string? NullIfEmpty(RedisValue value) => value.IsNullOrEmpty ? null : value.ToString();

    private static async Task<string> DisplayNameAsync(IDatabase redis, string id)
    {
        var fields = await redis.HashGetAsync($"connections:{id}", ["username", "hydraUsername"]);
        return fields.Select(f => f.ToString()).FirstOrDefault(f => f.Length > 0) ?? "Unknown";
    }

    private async Task<List<string>> KeysAsync(string pattern)
    {
        var multiplexer = _services.GetRequiredService<IConnectionMultiplexer>();
        var keys = new List<string>();
        await foreach (var key in RedisScan.KeysAsync(multiplexer, multiplexer.GetDatabase().Database, pattern, pageSize: 100))
        {
            keys.Add(key.ToString());
        }

        return keys;
    }

    private static async Task<JsonDocument?> JsonAsync(IDatabase redis, string key)
    {
        var raw = await redis.StringGetAsync(key);
        if (raw.IsNullOrEmpty)
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(raw.ToString());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? Str(BsonDocument document, string name) =>
        document.TryGetValue(name, out var v) && v.IsString ? v.AsString : null;
}

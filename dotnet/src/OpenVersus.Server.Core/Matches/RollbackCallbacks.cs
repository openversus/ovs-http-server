using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using OpenVersus.Server.Core.Compat;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Matches;

// The calls a rollback server and the players' P2P nodes make about one match, ported from the TS server's server.ts
// (/ovs_register, /mvsi_register, /ovs_end_match, /mvsi_end_match, /ovs_match_started, /ovs_p2p_ready, /ovs_p2p_failed),
// branch infinity-war. Each posts {matchId, key} (ovs-rollback-server, Utils/HTTPHelper.cs; the key is the one the game
// sent over UDP). A node runs on a player's machine and holds nothing else, so every call here is checked by the match
// key alone (matchKey in the match config, {matchId}), and answered "" when the match is unknown or the key differs. The
// MatchUpdateKey header they also carry is checked by match status (MatchStatusEvents), which only rollback servers send.
//
//   /ovs_register   the registry, for the rollback server, or for a node to learn its role: the humans and spectators,
//                   never the bots (they have no client to connect; the game makes them), max_players their count (the
//                   rollback server sizes its inputs from it and indexes them by player_index), match_duration 36000,
//                   each player's name and fighter from their connection ("Unknown" without). Sent as JSON signed over its
//                   exact bytes (X-OVS-Signature, INodeConfig: a node hosts only on a config the server signed; a relay
//                   ignores the header). Then, after the answer, the players are told to connect (game-server-instance-
//                   ready), except in a P2P match whose relay has not been asked for: there the host's node says when
//                   (/ovs_p2p_ready). "" is how a rollback server learns the match is not known, and it ends at once; an
//                   error object would start a broken match (it takes any JSON object as a config).
//   /mvsi_register  the older form, for MVSI rollback servers: every player, bots included, as {player_index, ip,
//                   is_host}; unsigned; the players told to connect at once.
//   /ovs_end_match, /mvsi_end_match   every player has left the rollback server (or a P2P host's node): match:end, which
//                   the TS websocket answers with the match's end. Results come from the game's
//                   submit_end_of_match_stats, never from here.
//   /ovs_match_started  a P2P host node's first frame (a rollback server reports it as a match status event instead):
//                   match_started:{match}, which keeps a mid-game disconnect from cancelling the match.
//   /ovs_p2p_ready  a P2P host node serves the match (every peer path open): the players are told to connect.
//   /ovs_p2p_failed a P2P node found no direct path: the first call asks for the relay (deployed on the match's port when
//                   rollback servers are deployed per match); every call is answered with its address {host, port}.
//
// Redis, read     {match} (the match config: players, matchKey, matchId, p2p, rollbackPort); connections:{player}
//                 username, character; match:{match} rollbackPort; p2p_relay:{match}
// Redis, written  match_started:{match} "1" EX 10 min; p2p_relay:{match} "1" NX EX 20 min
// Published       game_server_ready:notifications {containerMatchId, playerIds, resultId, rollbackPort (match:{match}'s, else
//                 Rollback:UdpPort)}: the TS websocket sends each player game-server-instance-ready (127.0.0.1 and their
//                 node's port in a P2P match); match:end {playersIds, matchId}
//
// Unlike there: whatever goes wrong is answered "" at once. TS threw on a request without matchId (the mvsi routes), on a
// stored config that is not JSON or has no players, and left the request unanswered until the caller gave up (a rollback
// server after 5 s, which then ended the same way).

/// <summary>game-server-instance-ready for <paramref name="PlayerIds"/> of <paramref name="MatchId"/>, and why.</summary>
public sealed record InstanceReady(string MatchId, JsonArray PlayerIds, string Reason);

/// <summary>A registry to send, its signature (null without a signing key), and who is told to connect once it is sent
/// (null: a P2P match, whose host node says when).</summary>
public sealed record RollbackRegistration(byte[] Body, string? Signature, InstanceReady? Ready);

public interface IRollbackCallbacks
{
    /// <summary>/ovs_register; null to answer "".</summary>
    Task<RollbackRegistration?> RegisterAsync(JsonNode? body);

    /// <summary>/mvsi_register; null to answer "".</summary>
    Task<RollbackRegistration?> RegisterLegacyAsync(JsonNode? body);

    /// <summary>Tells the players of <paramref name="ready"/> to connect.</summary>
    Task ReleaseAsync(InstanceReady ready);

    /// <summary>/ovs_end_match and /mvsi_end_match (<paramref name="route"/>, for the log).</summary>
    Task EndMatchAsync(JsonNode? body, string route);

    /// <summary>/ovs_match_started.</summary>
    Task MatchStartedAsync(JsonNode? body);

    /// <summary>/ovs_p2p_ready.</summary>
    Task P2PReadyAsync(JsonNode? body);

    /// <summary>/ovs_p2p_failed: the relay's address; null to answer "".</summary>
    Task<JsonObject?> P2PFailedAsync(JsonNode? body);
}

internal sealed class RollbackCallbacks(IServiceProvider services, IMatchLauncher launcher, INodeConfig nodeConfig,
    IOptionsMonitor<RollbackSettings> settings, ILogger<RollbackCallbacks> log) : IRollbackCallbacks
{
    public const string InstanceReadyChannel = "game_server_ready:notifications";
    public const string EndOfMatchChannel = "match:end";
    public const int MatchDuration = 36000;
    private static readonly TimeSpan s_startedTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan s_relayTtl = TimeSpan.FromMinutes(20);

    public async Task<RollbackRegistration?> RegisterAsync(JsonNode? body)
    {
        if (Redis() is not { } redis || await KeyedAsync(redis, body, "/ovs_register") is not { } match || Players(match, "/ovs_register") is not { } all)
        {
            return null;
        }

        var humans = all.Where(p => !Truthy(p["isBot"])).ToList();
        var players = new JsonArray();
        foreach (var p in humans)
        {
            RedisValue[]? connection = Text(p["playerId"]) is { } id ? await redis.HashGetAsync($"connections:{id}", ["username", "character"]) : null;
            var entry = new JsonObject();
            Copy(p, "playerIndex", entry, "player_index");
            Copy(p, "playerId", entry, "player_id");
            entry["player_name"] = connection?[0] is { IsNullOrEmpty: false } name ? name.ToString() : "Unknown";
            entry["player_character"] = connection?[1] is { IsNullOrEmpty: false } character ? character.ToString() : "Unknown";
            Copy(p, "ip", entry, "ip");
            Copy(p, "isHost", entry, "is_host");
            entry["is_spectator"] = p["isSpectator"]?.DeepClone() ?? false;
            players.Add(entry);
        }

        byte[] registry = Encoding.UTF8.GetBytes(Js.Stringify(new JsonObject
        {
            ["max_players"] = humans.Count,
            ["match_duration"] = MatchDuration,
            ["players"] = players,
        }));
        string? signature = nodeConfig.Sign(registry);
        log.LogInformation("Registry of match {Match}: {Players} player(s){Bots}, {Signed}", match.Id, humans.Count,
            all.Count > humans.Count ? $" ({all.Count - humans.Count} bot(s) left out)" : "", signature is null ? "unsigned (no signing key)" : "signed");

        if (Truthy(match.Config["p2p"]) && ((string?)await redis.StringGetAsync($"p2p_relay:{match.Id}")) is not { Length: > 0 })
        {
            log.LogInformation("Match {Match} is P2P: game-server-instance-ready waits for /ovs_p2p_ready from the host's node", match.Id);
            return new RollbackRegistration(registry, signature, null);
        }

        return new RollbackRegistration(registry, signature, new InstanceReady(match.Id, PlayerIds(humans), "the rollback server has the registry"));
    }

    public async Task<RollbackRegistration?> RegisterLegacyAsync(JsonNode? body)
    {
        if (Redis() is not { } redis || await KeyedAsync(redis, body, "/mvsi_register") is not { } match || Players(match, "/mvsi_register") is not { } all)
        {
            return null;
        }

        var players = new JsonArray();
        foreach (var p in all)
        {
            var entry = new JsonObject();
            Copy(p, "playerIndex", entry, "player_index");
            Copy(p, "ip", entry, "ip");
            Copy(p, "isHost", entry, "is_host");
            players.Add(entry);
        }

        byte[] registry = Encoding.UTF8.GetBytes(Js.Stringify(new JsonObject
        {
            ["max_players"] = all.Count,
            ["match_duration"] = MatchDuration,
            ["players"] = players,
        }));
        log.LogInformation("Registry of match {Match} for an MVSI rollback server: {Players} player(s), bots included", match.Id, all.Count);
        return new RollbackRegistration(registry, null, new InstanceReady(match.Id, PlayerIds(all), "the rollback server has the registry (legacy)"));
    }

    public async Task ReleaseAsync(InstanceReady ready)
    {
        if (Redis() is not { } redis)
        {
            return;
        }

        JsonNode port;
        if ((await JsonAsync(redis, $"match:{ready.MatchId}"))?["rollbackPort"] is { } own && Truthy(own))
        {
            port = own.DeepClone();
        }
        else
        {
            port = settings.CurrentValue.UdpPort;
            log.LogWarning("Match {Match} has no rollback port of its own (match:{Match}); game-server-instance-ready names Rollback:UdpPort (UDP_PORT) {Port}",
                ready.MatchId, ready.MatchId, settings.CurrentValue.UdpPort);
        }

        await redis.PublishAsync(RedisChannel.Literal(InstanceReadyChannel), Js.Stringify(new JsonObject
        {
            ["containerMatchId"] = ready.MatchId,
            ["playerIds"] = ready.PlayerIds.DeepClone(),
            ["resultId"] = ObjectId.GenerateNewId().ToString(),
            ["rollbackPort"] = port,
        }));
        log.LogInformation("Sent game-server-instance-ready for match {Match}: {Reason}", ready.MatchId, ready.Reason);
    }

    public async Task EndMatchAsync(JsonNode? body, string route)
    {
        if (Redis() is not { } redis || await KeyedAsync(redis, body, route) is not { } match || Players(match, route) is not { } all)
        {
            return;
        }

        var end = new JsonObject { ["playersIds"] = PlayerIds(all) };
        Copy(match.Config, "matchId", end, "matchId");
        await redis.PublishAsync(RedisChannel.Literal(EndOfMatchChannel), Js.Stringify(end));
        log.LogInformation("Match {Match} ended on its rollback server ({Route}): published match:end", match.Id, route);
    }

    public async Task MatchStartedAsync(JsonNode? body)
    {
        if (Redis() is not { } redis || await KeyedAsync(redis, body, "/ovs_match_started") is not { } match)
        {
            return;
        }

        await redis.StringSetAsync($"match_started:{match.Id}", "1", s_startedTtl);
        log.LogInformation("Match {Match} started (its host's node): a mid-game disconnect no longer cancels it", match.Id);
    }

    public async Task P2PReadyAsync(JsonNode? body)
    {
        if (Redis() is not { } redis || await KeyedAsync(redis, body, "/ovs_p2p_ready") is not { } match)
        {
            return;
        }

        if (!Truthy(match.Config["p2p"]))
        {
            log.LogInformation("/ovs_p2p_ready for match {Match}, which is not a P2P match; ignored", match.Id);
            return;
        }

        if (Players(match, "/ovs_p2p_ready") is not { } all)
        {
            return;
        }

        await ReleaseAsync(new InstanceReady(match.Id, PlayerIds(all.Where(p => !Truthy(p["isBot"]))), "the host's node is serving"));
    }

    public async Task<JsonObject?> P2PFailedAsync(JsonNode? body)
    {
        if (Redis() is not { } redis || await KeyedAsync(redis, body, "/ovs_p2p_failed") is not { } match)
        {
            return null;
        }

        if (!Truthy(match.Config["p2p"]))
        {
            log.LogInformation("/ovs_p2p_failed for match {Match}, which is not a P2P match; ignored", match.Id);
            return null;
        }

        var rollback = settings.CurrentValue;
        JsonNode? port = match.Config["rollbackPort"];
        if (await redis.StringSetAsync($"p2p_relay:{match.Id}", "1", s_relayTtl, When.NotExists))
        {
            log.LogInformation("P2P match {Match}: no direct path; relay on port {Port}{Deploying}", match.Id, port?.ToJsonString(), rollback.OnDemand ? " (deploying)" : "");
            if (rollback.OnDemand)
            {
                if (port is JsonValue value && value.TryGetValue(out int number) && number > 0)
                {
                    launcher.DeployIfOnDemand(number, match.Id);
                }
                else
                {
                    log.LogError("P2P match {Match} has no rollback port to deploy its relay on ({Port})", match.Id, port?.ToJsonString() ?? "none");
                }
            }
        }

        if (rollback.UdpServerIp.Length == 0)
        {
            log.LogError("No Rollback:UdpServerIp (UDP_SERVER_IP): the nodes of P2P match {Match} are named no relay, and the match will time out", match.Id);
        }

        var answer = new JsonObject { ["host"] = rollback.UdpServerIp };
        Copy(match.Config, "rollbackPort", answer, "port");
        return answer;
    }

    // ── The match a call names ─────────────────────────────────────────────────────────────────────────────────────

    private sealed record Keyed(string Id, JsonObject Config);

    // The match config of the body's matchId when the body's key is its matchKey; null (and a log line) otherwise.
    private async Task<Keyed?> KeyedAsync(IDatabase redis, JsonNode? body, string route)
    {
        if (body is not JsonObject request || Text(request["matchId"]) is not { } matchId || Text(request["key"]) is not { } key)
        {
            log.LogInformation("Invalid {Route} call: no matchId or key", route);
            return null;
        }

        if (await JsonAsync(redis, matchId) is not { } config || Text(config["matchKey"]) is not { } matchKey || matchKey != key)
        {
            log.LogInformation("Invalid {Route} call for match {Match}: unknown match or key mismatch", route, matchId);
            return null;
        }

        return new Keyed(matchId, config);
    }

    private List<JsonObject>? Players(Keyed match, string route)
    {
        if (match.Config["players"] is JsonArray players)
        {
            return [.. players.OfType<JsonObject>()];
        }

        log.LogWarning("{Route} for match {Match}: its config has no players", route, match.Id);
        return null;
    }

    private IDatabase? Redis()
    {
        if (services.GetService<IConnectionMultiplexer>()?.GetDatabase() is { } redis)
        {
            return redis;
        }

        log.LogError("Rollback call not handled: this service has no Redis (REDIS)");
        return null;
    }

    // ── JSON as JavaScript reads it ────────────────────────────────────────────────────────────────────────────────

    internal static async Task<JsonObject?> JsonAsync(IDatabase redis, string key)
    {
        if ((string?)await redis.StringGetAsync(key) is not { } raw)
        {
            return null;
        }

        try
        {
            return Js.Parse(raw) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    // The players' ids as JSON.stringify writes a mapped array: a missing id is null.
    internal static JsonArray PlayerIds(IEnumerable<JsonObject> players) => new([.. players.Select(p => p["playerId"]?.DeepClone())]);

    // from[key] into to[name] when from has it, as JSON.stringify writes {name: from.key}: left out when missing, null kept.
    internal static void Copy(JsonObject from, string key, JsonObject to, string name)
    {
        if (from.TryGetPropertyValue(key, out var value))
        {
            to[name] = value?.DeepClone();
        }
    }

    // A non-empty string (the ids and keys are truthy strings or nothing).
    internal static string? Text(JsonNode? value) => value is JsonValue v && v.TryGetValue(out string? s) && s.Length > 0 ? s : null;

    // JavaScript's truthiness.
    internal static bool Truthy(JsonNode? value) => value switch
    {
        null => false,
        JsonObject or JsonArray => true,
        JsonValue v => v.GetValueKind() switch
        {
            System.Text.Json.JsonValueKind.True => true,
            System.Text.Json.JsonValueKind.Number => Js.Number(v.ToJsonString()) is var d && d != 0 && !double.IsNaN(d),
            System.Text.Json.JsonValueKind.String => v.GetValue<string>().Length > 0,
            _ => false,
        },
        _ => false,
    };
}

public static class RollbackCallbacksHosting
{
    /// <summary>The rollback servers' and P2P nodes' calls about a match (and the match launcher and signing they need).</summary>
    public static WebApplicationBuilder AddRollbackCallbacks(this WebApplicationBuilder builder)
    {
        if (!builder.Services.Any(d => d.ServiceType == typeof(IMatchLauncher)))
        {
            builder.AddMatchLauncher();
        }

        if (!builder.Services.Any(d => d.ServiceType == typeof(INodeConfig)))
        {
            builder.AddNodeConfig();
        }

        builder.Services.TryAddSingleton<IRollbackCallbacks, RollbackCallbacks>();
        return builder;
    }
}

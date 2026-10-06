using System.ComponentModel;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.FunFacts;
using OpenVersus.Server.Core.Preferences;
using OpenVersus.Server.Core.Realtime;
using OpenVersus.Server.Core.Settings;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Matches;

// The party lobby the game fetches after create_party_lobby, ported from the TS server's PUT /matches/:id
// (handlers/matches.ts handleMatches_id, branch infinity-war). The game keeps its own lobby id (it sends it to
// matchmaking later) and ignores the answer's id. Three answers:
//   join           the lobby is someone else's: the player is added to it, and every member, the joiner and the owner
//                  included, is sent the lobby (below)
//   owner refresh  the player owns the lobby and it has 2+ players: everyone in it
//   solo           anything else: the player alone (the TS server's fixed answer, a new random id each time)
//
// Redis, read     lobby_redirect:{id} (a lobby id), player_lobby:{player} (a lobby id), lobby:{id} (JSON, below),
//                 connections:{player} (GameplayPreferences, hydraUsername, username, wb_network_id, character, skin),
//                 player:{player} (character, skin: the owner's and the members' loadouts)
// Redis, written  lobby:{id} on a join that adds the player: the JSON as read with the player pushed onto playerIds
//                 (other fields kept as they were), EX 8 h when it then has 2+ players, else 1 h
// Sent (ws:send)  on every join, to every member, the three messages the TS websocket built from
//                 lobby:player_joined (handlePlayerJoinedLobby): lobby-join {lobby, match, party_id, players}, then the
//                 updates OnLobbyRuntimeDataUpdated and PlayerJoinedLobby, each carrying the lobby and its players. Built
//                 from each member's connections:{player} (preferences, character, skin, names, wb id), one rand and one
//                 time for all three; the lobby's GameVersion is "local" and it names its MatchID, as there
// Client gate     none: a player who must update may join and be joined (the TS server refused the join); what leads into
//                 a match is gated (ClientGameplayGate, MatchmakingRequestService)
//
// lobby:{id} as the TS server writes it (ssc.ts, websocket.ts):
//   {"lobbyId", "ownerId", "ownerUsername", "mode" ("1v1", "2v2", "FFA", "1v1_ranked", "2v2_ranked"), "playerIds": [...],
//    "createdAt" (ms)}; one with no playerIds (an older custom lobby) is not joined or refreshed.
//
// GameplayPreferences: the player's stored value, 0 included, 964 only when there is none (Core/Preferences; the TS
// server does the same since 2026-09-30, before which a 0 became 964 and a missing value NaN).
//
// Differences from the TS server: a lobby:{id} that is not JSON is treated as no lobby (the TS request fails and never answers); a
// token with no id is refused (the TS server would build a lobby for the player "undefined"); a token claim that is
// missing is sent as "" (the TS server sends undefined, a NaN double where the game expects a string); each player's
// Steam entry carries their Steam id, else Epic id, else account id (the TS server sends 76561195177950873 for everyone),
// in the answer and in the messages a join sends.

/// <summary>What the lobby answers carry about the game build (GAME_VERSION).</summary>
public sealed class LobbySettings
{
    [Description("The game build the lobby answers state (GameVersion; GAME_VERSION).")]
    public string GameVersion { get; set; } = "195303.1.1";

    [Description("The address a player on this machine (127.0.0.1) is given to the matchmaker as, as the TS server's LOCAL_PUBLIC_IP does. Empty: 127.0.0.1 stays as it is.")]
    public string LocalPublicIp { get; set; } = "";
}

/// <summary>The player asking, from their session token.</summary>
public sealed record LobbyPlayer(string Id, string HydraUsername, string Username, string WbNetworkId, string SteamId = "", string EpicId = "");

public interface IPartyLobbyService
{
    /// <summary>The lobby answer for <paramref name="player"/> asking for <paramref name="matchId"/>; null when this service has no Redis.</summary>
    Task<JsonObject?> PutAsync(LobbyPlayer player, string matchId, CancellationToken ct = default);
}

internal sealed class PartyLobbyService(IServiceProvider services, IOptionsMonitor<LobbySettings> settings, TimeProvider time, ILogger<PartyLobbyService> log) : IPartyLobbyService
{
    private const string Avatar = "https://s3.amazonaws.com/wb-agora-hydra-ugc-dokken/identicons/identicon.584.png";
    // The TS server sends this avatar for every player.
    private const string SteamAvatar = "https://avatars.steamstatic.com/fef49e7fa7e1997310d705b2a6158ff8dc1cdfeb.jpg";

    public async Task<JsonObject?> PutAsync(LobbyPlayer player, string matchId, CancellationToken ct)
    {
        if (services.GetService<IConnectionMultiplexer>()?.GetDatabase() is not { } redis)
        {
            return null;
        }

        string me = player.Id;
        var connection = await HashAsync(redis, $"connections:{me}");
        if (connection.Count == 0 || !connection.ContainsKey("id"))
        {
            log.LogWarning("No Redis player connection found for player ID {Player}, cannot set loadout.", me);
        }

        // A lobby the player was force-joined into wins over the one they asked for.
        if (matchId.Length > 0 && await redis.StringGetAsync($"lobby_redirect:{matchId}") is { IsNullOrEmpty: false } redirect)
        {
            log.LogInformation("Lobby redirect: {From} -> {To} for player {Player}", matchId, (string?)redirect, me);
            matchId = redirect!;
        }

        if (await redis.StringGetAsync($"player_lobby:{me}") is { IsNullOrEmpty: false } assigned && (string?)assigned != matchId
            && await LobbyAsync(redis, assigned!) is { } assignedLobby && PlayerIds(assignedLobby) is { } assignedIds
            && assignedIds.Contains(me) && Str(assignedLobby, "ownerId") != me)
        {
            log.LogInformation("Player {Player} was force-joined into lobby {Lobby}, redirecting from {From}", me, (string?)assigned, matchId);
            matchId = assigned!;
        }

        var lobby = matchId.Length > 0 ? await LobbyAsync(redis, matchId) : null;
        var playerIds = lobby is null ? null : PlayerIds(lobby);
        if (lobby is not null && playerIds is null)
        {
            log.LogInformation("Lobby {Lobby} is a custom SSC lobby (no playerIds), skipping old join path", matchId);
        }
        else if (lobby is not null && playerIds is not null && Str(lobby, "ownerId") != me)
        {
            return await JoinAsync(redis, player, connection, matchId, lobby, playerIds);
        }

        if (lobby is not null && playerIds is { Count: > 1 } && Str(lobby, "ownerId") == me)
        {
            return await RefreshAsync(redis, player, matchId, lobby, playerIds);
        }

        return Solo(player, connection);
    }

    private async Task<JsonObject> JoinAsync(IDatabase redis, LobbyPlayer player, Dictionary<string, string> connection, string matchId, JsonObject lobby, List<string> playerIds)
    {
        string me = player.Id;
        string ownerId = Str(lobby, "ownerId") ?? "";
        log.LogInformation("Player {Player} ({Name}) joining existing lobby {Lobby} owned by {Owner}", me, player.Username, matchId, ownerId);
        var ids = lobby["playerIds"]!.AsArray();
        if (!playerIds.Contains(me))
        {
            ids.Add(me);
            playerIds.Add(me);
            var ttl = playerIds.Count >= 2 ? TimeSpan.FromHours(8) : TimeSpan.FromHours(1);
            await redis.StringSetAsync($"lobby:{matchId}", Js.Stringify(lobby), ttl);
            log.LogInformation("Saved lobby state for {Lobby} with players: {Players}", matchId, string.Join(", ", playerIds));
        }

        var ownerConnection = await HashAsync(redis, $"connections:{ownerId}");
        var ownerLoadout = await HashAsync(redis, $"player:{ownerId}");
        string mode = Or(Str(lobby, "mode"), "1v1");
        string ownerUsername = Str(lobby, "ownerUsername") ?? "";
        var now = time.GetUtcNow();

        await TellMembersAsync(redis, player, matchId, ownerId, Or(player.Username, player.HydraUsername, "Unknown"), playerIds, mode);

        string ownerName = Or(Get(ownerConnection, "username"), ownerUsername);
        var teamPlayers = new JsonObject
        {
            [ownerId] = TeamPlayer(ownerId, CreatedAt(lobby), 0),
            [me] = TeamPlayer(me, now.ToUnixTimeSeconds(), 1),
        };
        return Lobby(
            updatedAt: now.ToUnixTimeSeconds(), createdAt: CreatedAt(lobby), rand: Random.Shared.NextDouble(),
            ServerData(teamPlayers, teamLength: 2, leaderId: ownerId,
                gameplay: new JsonObject { [ownerId] = Preferences(ownerConnection), [me] = Preferences(connection) },
                autoParty: new JsonObject { [ownerId] = false, [me] = false },
                platforms: new JsonObject { [ownerId] = "PC", [me] = "PC" },
                loadouts: new JsonObject
                {
                    [ownerId] = Loadout(Or(Get(ownerLoadout, "character"), "character_shaggy"), Or(Get(ownerLoadout, "skin"), "skin_shaggy_default")),
                    [me] = Loadout(Or(Get(connection, "character"), "character_shaggy"), Or(Get(connection, "skin"), "skin_shaggy_default")),
                },
                modeString: mode, gameVersion: settings.CurrentValue.GameVersion),
            all: new JsonArray(
                Member(ownerId, hydraName: Or(Get(ownerConnection, "hydraUsername"), ownerUsername), wbId: Or(Get(ownerConnection, "wb_network_id"), ownerId), name: ownerName, hydraListed: Or(Get(ownerConnection, "hydraUsername"), ownerUsername), PlatformId(ownerId, ownerConnection)),
                Member(me, hydraName: player.HydraUsername, wbId: player.WbNetworkId, name: player.Username, hydraListed: player.HydraUsername, PlatformId(me, connection, player))),
            current: new JsonArray(ownerId, me), count: 2,
            templateAt: now.ToUnixTimeSeconds(), templateId: ObjectId.GenerateNewId().ToString(), id: matchId);
    }

    private async Task<JsonObject> RefreshAsync(IDatabase redis, LobbyPlayer player, string matchId, JsonObject lobby, List<string> playerIds)
    {
        log.LogInformation("OWNER REFRESH: Player {Player} ({Name}) refreshing their own lobby {Lobby} with {Count} players", player.Id, player.Username, matchId, playerIds.Count);
        var now = time.GetUtcNow();
        JsonObject teamPlayers = [], gameplay = [], autoParty = [], platforms = [], loadouts = [];
        var all = new JsonArray();
        for (int i = 0; i < playerIds.Count; i++)
        {
            string pid = playerIds[i];
            var connection = await HashAsync(redis, $"connections:{pid}");
            var loadout = await HashAsync(redis, $"player:{pid}");
            teamPlayers[pid] = TeamPlayer(pid, i == 0 ? CreatedAt(lobby) : now.ToUnixTimeSeconds(), i);
            gameplay[pid] = Preferences(connection);
            autoParty[pid] = false;
            platforms[pid] = "PC";
            loadouts[pid] = Loadout(Or(Get(loadout, "character"), "character_shaggy"), Or(Get(loadout, "skin"), "skin_shaggy_default"));
            all.Add(Member(pid,
                hydraName: Or(Get(connection, "hydraUsername"), Get(connection, "username"), "Unknown"),
                wbId: Or(Get(connection, "wb_network_id"), pid),
                name: Or(Get(connection, "username"), "Unknown"),
                hydraListed: Or(Get(connection, "hydraUsername"), "Unknown"),
                PlatformId(pid, connection)));
        }

        return Lobby(
            updatedAt: now.ToUnixTimeSeconds(), createdAt: CreatedAt(lobby), rand: Random.Shared.NextDouble(),
            ServerData(teamPlayers, teamLength: playerIds.Count, leaderId: Str(lobby, "ownerId") ?? "",
                gameplay, autoParty, platforms, loadouts,
                // playerIds.length >= 2 ? "2v2" : mode, and there are always 2+ here.
                modeString: "2v2", gameVersion: settings.CurrentValue.GameVersion),
            all, current: new JsonArray(lobby["playerIds"]!.AsArray().Select(n => n?.DeepClone()).ToArray()), count: playerIds.Count,
            templateAt: now.ToUnixTimeSeconds(), templateId: matchId, id: matchId);
    }

    // The TS server's original answer: fixed dates, rand and loadout, and new ids.
    private JsonObject Solo(LobbyPlayer player, Dictionary<string, string> connection)
    {
        string me = player.Id;
        var now = time.GetUtcNow();
        return Lobby(
            updatedAt: 1742265244, createdAt: 1742265244, rand: 0.6975513760957894,
            ServerData(new JsonObject { [me] = TeamPlayer(me, now.ToUnixTimeSeconds(), 0) }, teamLength: 1, leaderId: me,
                gameplay: new JsonObject { [me] = Preferences(connection) },
                autoParty: new JsonObject { [me] = false },
                platforms: new JsonObject { [me] = "PC" },
                loadouts: new JsonObject { [me] = Loadout("character_wonder_woman", "skin_wonder_woman_default") },
                modeString: "1v1", gameVersion: settings.CurrentValue.GameVersion),
            all: new JsonArray(Member(me, hydraName: player.HydraUsername, wbId: player.WbNetworkId, name: player.Username, hydraListed: player.HydraUsername, PlatformId(me, connection, player))),
            current: new JsonArray(me), count: 1,
            templateAt: now.ToUnixTimeSeconds(), templateId: ObjectId.GenerateNewId().ToString(), id: ObjectId.GenerateNewId().ToString());
    }

    /// <summary>The party_lobby match document all three answers and a join's messages share; its keys in the TS server's order.</summary>
    private static JsonObject Lobby(long updatedAt, long createdAt, double rand, JsonObject serverData,
        JsonArray all, JsonArray current, int count, long templateAt, string templateId, string id) => new()
    {
        ["updated_at"] = Date(updatedAt),
        ["created_at"] = Date(createdAt),
        ["account_id"] = null,
        ["completion_time"] = null,
        ["name"] = "white-green-wind-breeze-OS5dF",
        ["state"] = "open",
        ["access_level"] = "public",
        ["origin"] = "client",
        ["rand"] = rand,
        ["winning_team"] = new JsonArray(),
        ["win"] = new JsonArray(),
        ["loss"] = new JsonArray(),
        ["draw"] = null,
        ["arbitration"] = null,
        ["data"] = new JsonObject(),
        ["server_data"] = serverData,
        ["players"] = new JsonObject { ["all"] = all, ["current"] = current, ["count"] = count },
        ["matchmaking"] = null,
        ["cluster"] = "ec2-us-east-1-dokken",
        ["last_warning_time"] = null,
        ["template"] = new JsonObject
        {
            ["type"] = "async",
            ["name"] = "party_lobby",
            ["slug"] = "party_lobby",
            ["min_players"] = 2,
            ["max_players"] = 2,
            ["game_server_integration_enabled"] = false,
            ["game_server_config"] = null,
            ["created_at"] = Date(templateAt),
            ["updated_at"] = Date(templateAt),
            ["data"] = new JsonObject(),
            ["id"] = templateId,
        },
        ["criteria"] = new JsonObject { ["slug"] = null },
        ["shortcode"] = null,
        ["id"] = id,
        ["access"] = "public",
    };

    /// <summary>The lobby itself (server_data), its keys in the TS server's order; a join's messages also name its MatchID.</summary>
    private static JsonObject ServerData(JsonObject teamPlayers, int teamLength, string leaderId, JsonObject gameplay, JsonObject autoParty,
        JsonObject platforms, JsonObject loadouts, string modeString, string gameVersion, string? matchId = null)
    {
        var teams = new JsonArray(new JsonObject { ["TeamIndex"] = 0, ["Players"] = teamPlayers, ["Length"] = teamLength });
        for (int t = 1; t <= 4; t++)
        {
            teams.Add(new JsonObject { ["TeamIndex"] = t, ["Players"] = new JsonObject(), ["Length"] = 0 });
        }

        var data = new JsonObject
        {
            ["Teams"] = teams,
            ["LeaderID"] = leaderId,
            ["LobbyType"] = 0,
            ["ReadyPlayers"] = new JsonObject(),
            ["PlayerGameplayPreferences"] = gameplay,
            ["PlayerAutoPartyPreferences"] = autoParty,
            ["GameVersion"] = gameVersion,
            ["HissCrc"] = 1167552915,
            ["Platforms"] = platforms,
            ["AllMultiplayParams"] = new JsonObject
            {
                ["1"] = Multiplay("ec2-us-east-1-dokken", "1252499", ""),
                ["2"] = Multiplay("ec2-us-east-1-dokken", "1252922", "19c465a7-f21f-11ea-a5e3-0954f48c5682"),
                ["3"] = Multiplay("", "1252925", ""),
                ["4"] = Multiplay("ec2-us-east-1-dokken", "1252928", "19c465a7-f21f-11ea-a5e3-0954f48c5682"),
            },
            ["LockedLoadouts"] = loadouts,
            ["ModeString"] = modeString,
            ["IsLobbyJoinable"] = true,
        };
        if (matchId is not null)
        {
            data["MatchID"] = matchId;
        }

        return data;
    }

    // What a join sends every member (the TS websocket's handlePlayerJoinedLobby): the lobby with everyone on team 0 in
    // lobby order, each from their own session (connections:{player}), and the same three messages to each of them.
    private async Task TellMembersAsync(IDatabase redis, LobbyPlayer joiner, string lobbyId, string ownerId, string joinedUsername, List<string> memberIds, string mode)
    {
        long now = time.GetUtcNow().ToUnixTimeSeconds();
        JsonObject teamPlayers = [], gameplay = [], autoParty = [], platforms = [], loadouts = [];
        var connections = new Dictionary<string, Dictionary<string, string>>();
        for (int i = 0; i < memberIds.Count; i++)
        {
            string pid = memberIds[i];
            var connection = connections[pid] = await HashAsync(redis, $"connections:{pid}");
            teamPlayers[pid] = TeamPlayer(pid, now, i);
            gameplay[pid] = Preferences(connection);
            autoParty[pid] = false;
            platforms[pid] = "PC";
            loadouts[pid] = Loadout(Or(Get(connection, "character"), "character_shaggy"), Or(Get(connection, "skin"), "skin_shaggy_default"));
        }

        var lobby = ServerData(teamPlayers, memberIds.Count, ownerId, gameplay, autoParty, platforms, loadouts, mode, gameVersion: "local", matchId: lobbyId);
        var all = new JsonArray();
        foreach (string pid in memberIds)
        {
            var connection = connections[pid];
            string name = Or(Get(connection, "username"), Get(connection, "hydraUsername"), joinedUsername, "Unknown");
            string hydraName = Or(Get(connection, "hydraUsername"), name);
            all.Add(Member(pid, hydraName, wbId: Or(Get(connection, "wb_network_id"), pid), name, hydraListed: hydraName,
                PlatformId(pid, connection, pid == joiner.Id ? joiner : null)));
        }

        var players = new JsonObject { ["all"] = all, ["current"] = new JsonArray([.. memberIds.Select(id => (JsonNode)id)]), ["count"] = memberIds.Count };
        var match = Lobby(now, now, Random.Shared.NextDouble(), (JsonObject)lobby.DeepClone(), (JsonArray)all.DeepClone(),
            (JsonArray)players["current"]!.DeepClone(), memberIds.Count, now, lobbyId, lobbyId);
        var lobbyJoin = new JsonObject
        {
            ["data"] = new JsonObject(),
            ["payload"] = new JsonObject { ["lobby"] = lobby.DeepClone(), ["match"] = match, ["party_id"] = lobbyId, ["players"] = players.DeepClone() },
            ["header"] = "",
            ["cmd"] = "lobby-join",
        };
        var runtimeData = PlayerMessages.Update(new JsonObject
        {
            ["template_id"] = "OnLobbyRuntimeDataUpdated",
            ["LobbyId"] = lobbyId,
            ["ModeString"] = mode,
            ["lobby"] = lobby.DeepClone(),
            ["players"] = players.DeepClone(),
        }, new JsonObject { ["match"] = new JsonObject { ["id"] = lobbyId } });
        var joined = PlayerMessages.Update(new JsonObject
        {
            ["template_id"] = "PlayerJoinedLobby",
            ["LobbyId"] = lobbyId,
            ["ModeString"] = mode,
            ["lobby"] = lobby,
            ["JoinedPlayerId"] = joiner.Id,
            ["players"] = players,
        }, new JsonObject { ["match"] = new JsonObject { ["id"] = lobbyId } });
        foreach (var message in new[] { lobbyJoin, runtimeData, joined })
        {
            await PlayerMessages.SendAsync(redis, memberIds, message);
        }

        log.LogInformation("Sent lobby {Lobby} to its {Count} members (joined: {Player})", lobbyId, memberIds.Count, joiner.Id);
    }

    private static JsonObject Date(long seconds) => new() { ["_hydra_unix_date"] = seconds };

    private static JsonObject Multiplay(string cluster, string profile, string region) =>
        new() { ["MultiplayClusterSlug"] = cluster, ["MultiplayProfileId"] = profile, ["MultiplayRegionId"] = region };

    private static JsonObject TeamPlayer(string id, long joinedAt, int index) => new()
    {
        ["Account"] = new JsonObject { ["id"] = id },
        ["JoinedAt"] = Date(joinedAt),
        ["BotSettingSlug"] = "",
        ["LobbyPlayerIndex"] = index,
        ["CrossplayPreference"] = 1,
    };

    private static JsonObject Loadout(string character, string skin) => new() { ["Character"] = character, ["Skin"] = skin };

    /// <summary>
    /// The id a player's entry gives under Steam: their Steam id, else their Epic id, else their account id (from their
    /// session, else their token). Never the hardware or install id: those identify a player to this server (see
    /// AccountResolver) and would reach everyone in the lobby. "", "Unknown" and "ip_..." (placeholders /access
    /// replaces) are no id.
    /// </summary>
    internal static string PlatformId(string accountId, Dictionary<string, string> connection, LobbyPlayer? token = null)
    {
        static bool Real(string? id) => !string.IsNullOrEmpty(id) && id != "Unknown" && !id.StartsWith("ip_", StringComparison.Ordinal);
        foreach (string? id in new[] { Get(connection, "steamId"), token?.SteamId, Get(connection, "epicId"), token?.EpicId })
        {
            if (Real(id))
            {
                return id!;
            }
        }

        return accountId;
    }

    /// <summary>A player's entry in players.all: their Hydra name, and their name and id under Steam and the WB network.</summary>
    private static JsonObject Member(string id, string hydraName, string wbId, string name, string hydraListed, string platformId) => new()
    {
        ["account_id"] = id,
        ["source"] = new JsonObject(),
        ["state"] = "join",
        ["data"] = new JsonObject(),
        ["identity"] = new JsonObject
        {
            ["username"] = hydraName,
            ["avatar"] = Avatar,
            ["default_username"] = true,
            ["personal_data"] = new JsonObject(),
            ["alternate"] = new JsonObject
            {
                ["wb_network"] = new JsonArray(new JsonObject { ["id"] = wbId, ["username"] = name, ["avatar"] = null, ["email"] = null }),
                ["steam"] = new JsonArray(new JsonObject { ["id"] = platformId, ["username"] = name, ["avatar"] = SteamAvatar, ["email"] = null }),
            },
            ["usernames"] = new JsonArray(
                new JsonObject { ["auth"] = "hydra", ["username"] = hydraListed },
                new JsonObject { ["auth"] = "steam", ["username"] = name },
                new JsonObject { ["auth"] = "wb_network", ["username"] = name }),
            ["platforms"] = new JsonArray("steam"),
            ["current_platform"] = "steam",
            ["is_cross_platform"] = false,
        },
    };

    // The player's stored value, 0 included; 964 only when there is none (GameplayPreferences.Of, as the TS server's
    // gameplayPreferencesOf).
    private static JsonNode Preferences(Dictionary<string, string> connection) =>
        JsonValue.Create(GameplayPreferences.Of(Get(connection, "GameplayPreferences")));

    // MVSTime(new Date(createdAt)): whole seconds; a createdAt that is not a number is the TS server's NaN date, which
    // its encoder writes as 0.
    private static long CreatedAt(JsonObject lobby) =>
        lobby["createdAt"] is JsonValue v && v.TryGetValue<double>(out double ms) && double.IsFinite(ms) ? (long)Math.Floor(ms / 1000) : 0;

    private static async Task<Dictionary<string, string>> HashAsync(IDatabase redis, string key) =>
        (await redis.HashGetAllAsync(key)).ToDictionary(e => e.Name.ToString(), e => e.Value.ToString());

    private async Task<JsonObject?> LobbyAsync(IDatabase redis, string lobbyId)
    {
        if (await redis.StringGetAsync($"lobby:{lobbyId}") is not { HasValue: true } text)
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(text.ToString()) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            log.LogWarning("lobby:{Lobby} is not JSON; treated as no lobby", lobbyId);
            return null;
        }
    }

    // The lobby's playerIds as strings; null when it has none (not an array).
    private static List<string>? PlayerIds(JsonObject lobby) =>
        lobby["playerIds"] is JsonArray ids ? ids.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : Js.Stringify(n)).ToList() : null;

    private static string? Str(JsonObject obj, string key) => obj[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static string? Get(Dictionary<string, string> hash, string field) => hash.TryGetValue(field, out var value) ? value : null;

    // a || b || c for strings: the first non-empty.
    private static string Or(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? "";

}

public static class PartyLobbyHosting
{
    public static WebApplicationBuilder AddPartyLobbies(this WebApplicationBuilder builder)
    {
        builder.AddSetting<LobbySettings>("Lobbies");
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IPartyLobbyService, PartyLobbyService>();
        builder.Services.AddSingleton<IPartyService, PartyService>();
        builder.Services.AddFunFacts();
        return builder;
    }
}

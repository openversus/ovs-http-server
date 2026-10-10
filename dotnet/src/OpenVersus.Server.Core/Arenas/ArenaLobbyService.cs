using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Matches;
using OpenVersus.Server.Core.Preferences;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Arenas;

// PUT /ssc/invoke/create_arena_lobby: the Arena lobby, made when the player presses the mode select's Arena button
// (ArenaHiss). Every other Arena call starts from it. Neither the TS server nor any capture answers it; the request was
// first seen on the bench (2026-10-10): create_party_lobby's body with LobbyTemplate "arena_lobby".
// What the answer must hold: UMvsArenaLobby keeps its base class's lobby parser (UMvsPlayerLobby, the virtual at
// 0x1428e9290, build f97148ff), which requires body.lobby and in it MatchID, LeaderID, ReadyPlayers and Teams, each team
// parsed (0x1428e7d10); GameModeSlug is optional. The lobby screen (WBP_ArenaLobbyWidget) shows two rows of four teams
// (the second row starts at team index 4) of two players each: Teams holds eight, indexes 0 to 7. The rest of the lobby
// is create_party_lobby's, as the rift lobby's is, so the party lobby code that reads lobby:{id} sees an ordinary lobby.
//
// Redis, read     connections:{player} (GameplayPreferences, username, hydraUsername), player:{player} (character, skin)
// Redis, written  lobby:{id} (JSON, mode "arena_lobby") EX 1 h; player_lobby:{player} = id EX 8 h;
//                 player:{player}:lobby:{id} hash (id, created_at, mode, owner); connections:{player} lobby_id = id
//
// PUT /ssc/invoke/lobby_code from an Arena lobby (the lobby screen's eye button; the route is the custom lobby's, which
// asks here first): the lobby's code, for any member (the custom lobby gives its leader one). The first ask draws it,
// every later one gets the same code. Codes share the custom lobbies' namespace (LobbyCodes), so a code typed in either
// lobby's box names one lobby.
// Redis, read     lobby:{id}; arena_lobby_code:{id}
// Redis, written  lobby_code:{CODE} = id and arena_lobby_code:{id} = CODE, SET NX, for as long as lobby:{id} has left

public interface IArenaLobbyService
{
    /// <summary>The create_arena_lobby answer for the player the session token (<paramref name="claims"/>) names.</summary>
    Task<JsonObject> CreateAsync(JsonObject? claims, JsonObject? request, CancellationToken ct);

    /// <summary>
    /// The lobby_code answer when <paramref name="request"/>'s LobbyId is an Arena lobby ({LobbyCode}, null for someone not
    /// in it), else null: the request is another lobby's.
    /// </summary>
    Task<JsonObject?> LobbyCodeAsync(string playerId, JsonObject? request, CancellationToken ct);
}

internal sealed class ArenaLobbyService(IServiceProvider services, IOptionsMonitor<LobbySettings> lobbies, TimeProvider time,
    ILogger<ArenaLobbyService> log) : IArenaLobbyService
{
    public const string Mode = "arena_lobby";

    /// <summary>The lobby's teams: eight of two (WB: "8 teams of 2").</summary>
    public const int TeamCount = 8;

    public async Task<JsonObject> CreateAsync(JsonObject? claims, JsonObject? request, CancellationToken ct)
    {
        string? playerId = claims?["id"] is JsonValue v && v.TryGetValue(out string? id) && id.Length > 0 ? id : null;
        if (playerId is null || services.GetService<IConnectionMultiplexer>()?.GetDatabase() is not { } redis)
        {
            log.LogWarning("create_arena_lobby with no player id or no Redis; answered empty");
            return LobbyDocuments.Ssc([]);
        }

        var connection = (await redis.HashGetAllAsync($"connections:{playerId}")).ToDictionary(e => e.Name.ToString(), e => e.Value.ToString());
        var player = (await redis.HashGetAllAsync($"player:{playerId}")).ToDictionary(e => e.Name.ToString(), e => e.Value.ToString());
        string lobbyId = ObjectId.GenerateNewId().ToString();
        var now = time.GetUtcNow();

        var state = new JsonObject
        {
            ["lobbyId"] = lobbyId,
            ["ownerId"] = playerId,
            ["ownerUsername"] = Or(Get(connection, "username"), Get(connection, "hydraUsername"), "Unknown"),
            ["mode"] = Mode,
            ["playerIds"] = new JsonArray(playerId),
            ["createdAt"] = now.ToUnixTimeMilliseconds(),
        };
        await redis.StringSetAsync($"lobby:{lobbyId}", Js.Stringify(state), TimeSpan.FromHours(1));
        await redis.StringSetAsync($"player_lobby:{playerId}", lobbyId, TimeSpan.FromHours(8));
        await redis.HashSetAsync($"player:{playerId}:lobby:{lobbyId}",
        [
            new HashEntry("id", lobbyId),
            new HashEntry("created_at", now.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")),
            new HashEntry("mode", Mode),
            new HashEntry("owner", playerId),
        ]);
        await redis.HashSetAsync($"connections:{playerId}", "lobby_id", lobbyId);
        log.LogInformation("Created arena lobby {Lobby} for {Player}", lobbyId, playerId);

        // The value the game sends (the player's current one), else the stored one: 0 included, 964 only when there is none.
        JsonNode preferences = GameplayPreferences.Parse(request?["GameplayPreferences"]) is { } sent
            ? JsonValue.Create(sent)
            : JsonValue.Create(GameplayPreferences.Of(Get(connection, "GameplayPreferences")));
        var lobby = Lobby(lobbyId, playerId, now.ToUnixTimeSeconds(), request, preferences,
            Or(Get(player, "character"), "character_shaggy"), Or(Get(player, "skin"), "skin_shaggy_default"), lobbies.CurrentValue.GameVersion);
        return LobbyDocuments.Ssc(new JsonObject { ["lobby"] = lobby, ["Cluster"] = LobbyDocuments.Cluster });
    }

    /// <summary>
    /// A new Arena lobby with its creator alone on team 0, the other seven teams empty; the request's own values where it
    /// sends them (LobbyType, AutoPartyPreference, HissCrc, Platform, AllMultiplayParams).
    /// </summary>
    internal static JsonObject Lobby(string lobbyId, string playerId, long nowSeconds, JsonObject? request, JsonNode preferences,
        string character, string skin, string gameVersion)
    {
        var teams = new JsonArray(new JsonObject
        {
            ["TeamIndex"] = 0,
            ["Players"] = new JsonObject { [playerId] = LobbyDocuments.TeamPlayer(playerId, nowSeconds, 0) },
            ["Length"] = 1,
        });
        for (int t = 1; t < TeamCount; t++)
        {
            teams.Add(new JsonObject { ["TeamIndex"] = t, ["Players"] = new JsonObject(), ["Length"] = 0 });
        }

        return new JsonObject
        {
            ["Teams"] = teams,
            ["LeaderID"] = playerId,
            ["LobbyType"] = request?["LobbyType"]?.DeepClone() ?? 0,
            ["ReadyPlayers"] = new JsonObject(),
            ["PlayerGameplayPreferences"] = new JsonObject { [playerId] = preferences.DeepClone() },
            ["PlayerAutoPartyPreferences"] = new JsonObject { [playerId] = request?["AutoPartyPreference"]?.DeepClone() ?? false },
            ["GameVersion"] = gameVersion,
            ["HissCrc"] = request?["HissCrc"]?.DeepClone() ?? 0,
            ["Platforms"] = new JsonObject { [playerId] = request?["Platform"]?.DeepClone() ?? "PC" },
            ["AllMultiplayParams"] = request?["AllMultiplayParams"]?.DeepClone() ?? LobbyDocuments.AllMultiplay(),
            ["LockedLoadouts"] = new JsonObject { [playerId] = LobbyDocuments.Loadout(character, skin) },
            // A guess: no answer from WB is known, and the lobby parser does not read it.
            ["ModeString"] = Mode,
            ["IsLobbyJoinable"] = true,
            ["MatchID"] = lobbyId,
            ["LobbyTemplate"] = Mode,
        };
    }

    public async Task<JsonObject?> LobbyCodeAsync(string playerId, JsonObject? request, CancellationToken ct)
    {
        string lobbyId = request?["LobbyId"] is JsonValue v && v.TryGetValue(out string? id) ? id : "";
        if (lobbyId.Length == 0 || services.GetService<IConnectionMultiplexer>()?.GetDatabase() is not { } redis)
        {
            return null;
        }

        var stored = await redis.StringGetAsync($"lobby:{lobbyId}");
        if (stored.IsNullOrEmpty || Js.Parse(stored.ToString()) is not JsonObject state || state["mode"]?.ToString() != Mode)
        {
            return null;
        }

        if (!(state["playerIds"] as JsonArray ?? []).Any(p => p?.ToString() == playerId))
        {
            log.LogWarning("lobby_code for arena lobby {Lobby} from {Player}, who is not in it", lobbyId, playerId);
            return LobbyDocuments.Ssc(new JsonObject { ["LobbyCode"] = null });
        }

        string codeKey = CodeKey(lobbyId);
        if (await redis.StringGetAsync(codeKey) is { IsNullOrEmpty: false } existing)
        {
            return LobbyDocuments.Ssc(new JsonObject { ["LobbyCode"] = existing.ToString() });
        }

        var ttl = await redis.KeyTimeToLiveAsync($"lobby:{lobbyId}") ?? TimeSpan.FromHours(1);
        for (int attempt = 0; attempt < 5; attempt++)
        {
            string drawn = LobbyCodes.Draw();
            if (!await redis.StringSetAsync(LobbyCodes.Key(drawn), lobbyId, ttl, When.NotExists))
            {
                continue;
            }

            if (await redis.StringSetAsync(codeKey, drawn, ttl, When.NotExists))
            {
                log.LogInformation("Arena lobby {Lobby} has code {Code}", lobbyId, drawn);
                return LobbyDocuments.Ssc(new JsonObject { ["LobbyCode"] = drawn });
            }

            // Another ask drew the lobby's code first: that one stands.
            await redis.KeyDeleteAsync(LobbyCodes.Key(drawn));
            return LobbyDocuments.Ssc(new JsonObject { ["LobbyCode"] = (await redis.StringGetAsync(codeKey)).ToString() });
        }

        log.LogWarning("No free lobby code for arena lobby {Lobby} in 5 draws", lobbyId);
        return LobbyDocuments.Ssc(new JsonObject { ["LobbyCode"] = null });
    }

    internal static string CodeKey(string lobbyId) => $"arena_lobby_code:{lobbyId}";

    private static string? Get(Dictionary<string, string> hash, string field) => hash.TryGetValue(field, out var value) ? value : null;

    private static string Or(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? "";
}

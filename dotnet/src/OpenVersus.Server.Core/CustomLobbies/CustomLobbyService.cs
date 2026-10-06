using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using OpenVersus.Server.Core.Clients;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Matches;
using OpenVersus.Server.Core.Preferences;
using OpenVersus.Server.Core.Realtime;
using OpenVersus.Server.Core.Settings;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.CustomLobbies;

// The custom lobby (the game's custom game screen: teams, modes, maps, bots, handicaps, buffs, lobby codes, starting the
// match), ported from the TS server's modules/customLobby (lobby.routes.ts, lobby.service.ts) and the custom lobby side
// of modules/lobby/shared.routes.ts. The messages the TS server published on custom_lobby:notification for its websocket
// to relay go through ws:send (PlayerMessages), unchanged.
//
// Every change to a lobby is one Lua script (Scripts/*.lua, run with EVAL): read the lobby's JSON, decode it with cjson,
// change it, encode it, write it back, in one step, so two players changing a lobby at once cannot lose a change, on one
// server or several. The scripts started as the TS server's, byte for byte (tools/customlobby/gen_scripts.mjs; --check
// lists the ones changed since). cjson writes an empty object and an empty array alike, so what a script returns, and
// any lobby read, goes through the TS server's repair (FixEmptyTables). cjson also writes keys in its own order: the
// game reads the lobby by key, and has been taking that order all along.
//
// Redis, the TS server's keys (MIGRATION-BRIDGES.md 2; the TS match end and rematch read them while MatchEnd:Enabled is off):
//   custom_lobby_ssc:{lobby}            the lobby, JSON; EX 2 days (every change renews it)
//   ssc_custom_lobby_player:{player}    the lobby a player is in; EX 2 days, 20 min once a match starts
//   lobby_code:{code}                   the lobby a code names, SET NX; EX 2 days (the last player out deletes it)
//   ssc_custom_lobby_match:{match}      the lobby a match came from (match end, rematch); EX 20 min
//   bot_config:{bot}                    a bot's character, skin and difficulty for the match; EX 1 day
//   connections:{player} (+ its IP copy) character and skin written at the start; read with player:{player} for the
//                                       players' settings
// The match itself is started by IMatchLauncher (match:notifications, then matchmaking-complete to the players), as the TS server did.
//
// Differences from the TS server (each asserted by tools/matches/custom_lobby_diff.mjs):
//   - a player who joins a lobby they are already in is answered the lobby; the TS server added them again, in another
//     team, and the lobby could then never be all ready.
//   - a loadout lock and a lobby code are scripts too; the TS server rewrote the whole lobby outside any script, and a
//     ready that landed in between was lost (11 and 12 of 20 in two recordings).
//   - when the leader leaves, the lead passes to the remaining player who joined first, whatever their team; the TS
//     server gave it to the first player of the first team that had one, a bot included. A lobby with no player left
//     (bots only) goes.
//   - only the leader can promote, and only a player (not a bot); the TS server let anyone promote anyone.
//   - a player can move to any team the team style has room in; the TS server let them change teams only in Duos (or
//     through the spectators).
//   - lobby:joined (published on create and join) is not published: nothing subscribes to it.
//   - a lobby code that is already taken is drawn again; the TS server gave the lobby the taken code.
//   - a match's end takes the ready flags down with a script (MatchEndedAsync); the TS match end rewrote the lobby
//     outside any script.
//   - a rematch starts only while a player (not a bot) is left in the lobby's teams 0-3 (RematchAsync, Rematches); the
//     TS server started one with nobody to play it, and the game waited forever.
// Not yet: a player who disconnects is taken out of their lobby by the TS websocket with the TS leave script (the old
// succession); that moves with the websocket.
// Kept as the TS server has them, on purpose: who counts toward bAllPlayersReady (spectators' flags count, spectators
// are not in the total, bots are and never ready), LobbyPlayerIndex (the count at join: it can repeat after a leave; it
// is the lobby screen's, the match's player indexes are worked out at the start), and the start's player indexes and
// spectator entries (the rollback server depends on them).

/// <summary>Custom lobby settings.</summary>
public sealed class CustomLobbySettings
{
    [Description("Milliseconds set_game_mode_for_custom_game waits before changing the mode. The TS server waited 1500, for a reason nobody remembers; 0 until something shows it is needed.")]
    [Range(0, 10000)]
    public int GameModeDelayMs { get; set; }
}

public interface ICustomLobbyService
{
    /// <summary>The answer to a custom lobby route (create_custom_game_lobby, join_custom_game_lobby, ...), as the TS server gave it.</summary>
    Task<JsonObject> AnswerAsync(string route, PartyRequest request, CancellationToken ct = default);

    /// <summary>
    /// The answer to a shared lobby route (create_party_lobby, leave_player_lobby, invite_to_player_lobby,
    /// lock_lobby_loadout, set_ready_for_lobby) for the custom lobby <paramref name="lobbyId"/>; null when the party
    /// route should answer instead (the TS server fell through to it when its custom lobby side failed).
    /// </summary>
    Task<JsonObject?> SharedAsync(string route, PartyRequest request, string lobbyId, CancellationToken ct = default);

    /// <summary>GET /matches/{code}: the match document of the lobby a code (10 characters or fewer, any case) names, or null.</summary>
    Task<JsonObject?> ByCodeAsync(string code, CancellationToken ct = default);

    /// <summary>
    /// The lobby's rematch: its match started again as its leader's start_custom_match would. False when it cannot be
    /// (no lobby, no player but bots left in teams 0-3, a client that must update, no rollback port).
    /// </summary>
    Task<bool> RematchAsync(string lobbyId, CancellationToken ct = default);

    /// <summary>The routes <see cref="AnswerAsync"/> answers.</summary>
    static readonly IReadOnlyList<string> Routes =
    [
        "create_custom_game_lobby", "join_custom_game_lobby", "update_team_style_for_custom_game", "update_int_setting_for_custom_game",
        "set_game_mode_for_custom_game", "set_enabled_maps_for_custom_game", "set_player_handicap_for_custom_game",
        "switch_custom_game_lobby_team", "add_custom_game_bot", "update_custom_game_bot_fighter", "reset_custom_lobby_to_defaults",
        "promote_to_lobby_leader", "kick_from_lobby", "set_world_buffs_for_custom_game", "lobby_code", "start_custom_match",
    ];
}

internal sealed class CustomLobbyService(IServiceProvider services, IMatchLauncher launcher, IClientUpdateGate gate, IOptionsMonitor<LobbySettings> lobbySettings,
    IOptionsMonitor<CustomLobbySettings> settings, TimeProvider time, ILogger<CustomLobbyService> log) : ICustomLobbyService
{
    private static readonly TimeSpan s_lobbyTtl = TimeSpan.FromDays(2);
    private static readonly TimeSpan s_matchTtl = TimeSpan.FromMinutes(20);
    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const string Cluster = LobbyDocuments.Cluster;

    // The fields that must be an object, and those that must be an array, where cjson wrote an empty one (fixCjsonEmptyTables).
    private static readonly HashSet<string> s_objectFields =
    [
        "Players", "ReadyPlayers", "PlayerGameplayPreferences", "PlayerAutoPartyPreferences", "Platforms", "LockedLoadouts",
        "Handicaps", "PlayerBuffs", "AllMultiplayParams", "Spectators", "CustomGameSettings", "HudSettings",
    ];

    private static readonly HashSet<string> s_arrayFields =
    [
        "WorldBuffs", "Maps", "Teams", "Taunts", "Perks", "Buffs", "StatTrackers", "Gems", "Bundles", "TeamData",
        "MapsInRotation", "RequiredWorldBuffs", "RequiredTeamPlayerBuffs", "RequiredPlayerBuffs",
    ];

    // Bots (data/botDefaults.ts): the perks locked for them at the start, and each setting's difficulty.

    private static readonly Lazy<Dictionary<string, string>> s_scripts = new(() =>
    {
        var assembly = typeof(CustomLobbyService).Assembly;
        const string prefix = "OpenVersus.Server.Core.CustomLobbies.Scripts.";
        return assembly.GetManifestResourceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal)).ToDictionary(
            n => n[prefix.Length..^".lua".Length],
            n => new StreamReader(assembly.GetManifestResourceStream(n)!).ReadToEnd());
    });

    private IDatabase Redis() => services.GetService<IConnectionMultiplexer>()?.GetDatabase() ?? throw new InvalidOperationException("this service has no Redis (REDIS)");

    // ── The routes ──────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<JsonObject> AnswerAsync(string route, PartyRequest request, CancellationToken ct)
    {
        var body = request.Body ?? [];
        try
        {
            return route switch
            {
                "create_custom_game_lobby" => await CreateAsync(request.AccountId),
                "join_custom_game_lobby" => await JoinAsync(request.AccountId, body),
                "update_team_style_for_custom_game" => await TeamStyleAsync(request.AccountId, body),
                "update_int_setting_for_custom_game" => await IntSettingAsync(request.AccountId, body),
                "set_game_mode_for_custom_game" => await GameModeAsync(request.AccountId, body, ct),
                "set_enabled_maps_for_custom_game" => await MapsAsync(request.AccountId, body),
                "set_player_handicap_for_custom_game" => await HandicapAsync(request.AccountId, body),
                "switch_custom_game_lobby_team" => await SwitchTeamAsync(request.AccountId, body),
                "add_custom_game_bot" => await AddBotAsync(request.AccountId, body),
                "update_custom_game_bot_fighter" => await BotFighterAsync(request.AccountId, body),
                "reset_custom_lobby_to_defaults" => await ResetAsync(request.AccountId, body),
                "promote_to_lobby_leader" => await PromoteAsync(request.AccountId, body),
                "kick_from_lobby" => await KickAsync(request.AccountId, body),
                "set_world_buffs_for_custom_game" => await WorldBuffsAsync(request.AccountId, body),
                "lobby_code" => await LobbyCodeAsync(request.AccountId, body),
                "start_custom_match" => await StartAsync(request.AccountId, body, ct),
                _ => throw new ArgumentOutOfRangeException(nameof(route), route, "not a custom lobby route"),
            };
        }
        catch (Exception e) when (e is RedisException or JsonException or InvalidOperationException or UnknownGameModeException or MissingFieldException)
        {
            // What each TS route's catch answered.
            log.LogError("{Route} failed for {Player}: {Error}", route, request.AccountId, e.Message);
            return route switch
            {
                "switch_custom_game_lobby_team" => [],
                "update_team_style_for_custom_game" or "update_int_setting_for_custom_game" or "set_game_mode_for_custom_game"
                    or "set_enabled_maps_for_custom_game" or "set_player_handicap_for_custom_game" => LobbyDocuments.Ssc([]),
                _ => LobbyDocuments.Ssc([], 1),
            };
        }
    }

    public async Task<JsonObject?> SharedAsync(string route, PartyRequest request, string lobbyId, CancellationToken ct)
    {
        var body = request.Body ?? [];
        try
        {
            return route switch
            {
                "create_party_lobby" => await GetLobbyAsync(Redis(), lobbyId) is { } lobby ? LobbyDocuments.Answer(lobby) : null,
                "leave_player_lobby" => LobbyDocuments.Ssc(new JsonObject { ["lobby"] = await LeaveAsync(lobbyId, request.AccountId) }),
                "invite_to_player_lobby" => await InviteAsync(lobbyId, request.AccountId, body),
                "lock_lobby_loadout" => await LockLoadoutAsync(lobbyId, request.AccountId, body),
                "set_ready_for_lobby" => new JsonObject { ["body"] = await SetReadyAsync(lobbyId, request.AccountId, body), ["metadata"] = null, ["return_code"] = 0 },
                _ => throw new ArgumentOutOfRangeException(nameof(route), route, "not a shared lobby route"),
            };
        }
        catch (Exception e) when (e is RedisException or JsonException or InvalidOperationException or UnknownGameModeException or MissingFieldException)
        {
            log.LogError("{Route} for custom lobby {Lobby} failed for {Player}: {Error}", route, lobbyId, request.AccountId, e.Message);
            // set_ready_for_lobby answered from its catch; the others fell through to the party route.
            return route == "set_ready_for_lobby" ? LobbyDocuments.Ssc([]) : null;
        }
    }

    // ── create_custom_game_lobby ────────────────────────────────────────────────────────────────────────────────────
    private async Task<JsonObject> CreateAsync(string me)
    {
        var redis = Redis();
        var lobby = await BaseLobbyAsync(redis, me);
        lobby["ReadyPlayers"] = new JsonObject { [me] = true };
        foreach (var (key, value) in GameModes.DefaultSettings(GameModes.Default))
        {
            lobby[key] = value?.DeepClone();
        }

        string id = Str(lobby, "MatchID")!;
        await redis.StringSetAsync(LobbyKey(id), Js.Stringify(lobby), s_lobbyTtl);
        await redis.StringSetAsync(PlayerKey(me), id, s_lobbyTtl);
        log.LogInformation("Created custom lobby {Lobby} for {Player}", id, me);
        return LobbyDocuments.Answer(lobby);
    }

    // ── join_custom_game_lobby ──────────────────────────────────────────────────────────────────────────────────────
    private async Task<JsonObject> JoinAsync(string me, JsonObject body)
    {
        var redis = Redis();
        string lobbyId = ArgString(body["HostId"]);
        var config = await PlayerConfigAsync(redis, me);
        var result = await EvalAsync(redis, "join_custom_lobby", [LobbyKey(lobbyId)],
            [me, Truthy(body["IsSpectator"]) ? "true" : "false", IsoNow(), Js.Stringify(LobbyDocuments.Loadout(config.Character, config.Skin)), config.Preferences.ToString(CultureInfo.InvariantCulture)]);
        if (result.IsNull || (string?)result == "false")
        {
            log.LogInformation("join_custom_game_lobby: lobby {Lobby} not found or not joinable", lobbyId);
            return LobbyDocuments.Ssc([], 1);
        }

        if ((string?)result == "member")
        {
            // Already in it: the lobby as it is, and no news for the others.
            await redis.StringSetAsync(PlayerKey(me), lobbyId, s_lobbyTtl);
            return await GetLobbyAsync(redis, lobbyId) is { } current ? JoinAnswer(current) : LobbyDocuments.Ssc([], 1);
        }

        var lobby = Parse(result) as JsonObject ?? throw new JsonException("join_custom_lobby returned no lobby");
        await RefreshBuffsAsync(redis, lobbyId, Str(lobby, "GameModeSlug"));
        var updated = await GetLobbyAsync(redis, lobbyId);
        var team = Teams(lobby).FirstOrDefault(t => Players(t)[me] is not null);
        string cluster = (lobby["AllMultiplayParams"]?["1"]?["MultiplayClusterSlug"] as JsonValue)?.ToString() ?? "";
        await redis.StringSetAsync(PlayerKey(me), Str(lobby, "MatchID") ?? lobbyId, s_lobbyTtl);
        if (updated is null)
        {
            return LobbyDocuments.Ssc([], 1);
        }

        var data = new JsonObject { ["MatchID"] = lobbyId, ["template_id"] = "PlayerJoinedLobby" };
        if (team is not null)
        {
            data["Player"] = Players(team)[me]?.DeepClone();
            data["TeamIndex"] = team["TeamIndex"]?.DeepClone();
        }

        data["Cluster"] = cluster;
        data["LockedLoadouts"] = lobby["LockedLoadouts"]?.DeepClone();
        data["ModeString"] = null;
        await SendAsync(redis, Ids(lobby, "PlayerGameplayPreferences"), [me], Notice(data, lobbyId));
        return JoinAnswer(updated);
    }

    private static JsonObject JoinAnswer(JsonObject lobby) => LobbyDocuments.Ssc(new JsonObject
    {
        ["lobby"] = lobby,
        ["Cluster"] = Cluster,
        ["bIsJoiningCrossPlatform"] = false,
        ["ConnectionQuality"] = 0,
    });

    // ── update_team_style_for_custom_game ───────────────────────────────────────────────────────────────────────────
    private async Task<JsonObject> TeamStyleAsync(string me, JsonObject body)
    {
        var redis = Redis();
        string lobbyId = ArgString(body["MatchID"]);
        string? style = body["TeamStyle"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        var lobby = await ApplyGameSettingsAsync(redis, lobbyId, me, GameModes.ForTeamStyle(style));
        if (lobby is not null)
        {
            await SendAsync(redis, Ids(lobby, "PlayerGameplayPreferences"), [me],
                Notice(new JsonObject { ["template_id"] = "TeamStyleChangedForCustomGame", ["MatchID"] = lobbyId, ["lobby"] = lobby.DeepClone() }, lobbyId));
        }

        return LobbyDocuments.Ssc(new JsonObject { ["lobby"] = lobby });
    }

    // ── update_int_setting_for_custom_game ──────────────────────────────────────────────────────────────────────────
    private async Task<JsonObject> IntSettingAsync(string me, JsonObject body)
    {
        var redis = Redis();
        string lobbyId = ArgString(body["MatchID"]);
        string key = ArgString(body["SettingKey"]);
        var value = Required(body, "SettingValue");
        var result = await EvalAsync(redis, "update_int_setting", [LobbyKey(lobbyId)], [me, key, Js.Stringify(value)]);
        if (!result.IsNull && Parse(result) is JsonObject lobby)
        {
            await SendAsync(redis, Ids(lobby, "PlayerGameplayPreferences"), [me], Notice(new JsonObject
            {
                ["template_id"] = "IntSettingUpdatedForCustomGame",
                ["MatchID"] = lobbyId,
                ["SettingKey"] = body["SettingKey"]?.DeepClone(),
                ["SettingValue"] = value.DeepClone(),
            }, lobbyId));
        }

        // The request, as it came.
        return LobbyDocuments.Ssc((JsonObject)body.DeepClone());
    }

    // ── set_game_mode_for_custom_game ───────────────────────────────────────────────────────────────────────────────
    private async Task<JsonObject> GameModeAsync(string me, JsonObject body, CancellationToken ct)
    {
        var redis = Redis();
        if (settings.CurrentValue.GameModeDelayMs > 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(settings.CurrentValue.GameModeDelayMs), time, ct);
        }

        string lobbyId = ArgString(body["MatchID"]);
        string slug = body["GameModeSlug"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : throw new UnknownGameModeException(body["GameModeSlug"]?.ToJsonString() ?? "undefined", "is not a game mode");
        var lobby = await ApplyGameSettingsAsync(redis, lobbyId, me, slug);
        if (lobby is not null)
        {
            await SendAsync(redis, Ids(lobby, "PlayerGameplayPreferences"), [me],
                Notice(new JsonObject { ["template_id"] = "GameModeUpdatedForCustomGame", ["MatchID"] = lobbyId, ["lobby"] = lobby.DeepClone() }, lobbyId));
        }

        return LobbyDocuments.Ssc(new JsonObject { ["lobby"] = lobby });
    }

    // ── set_enabled_maps_for_custom_game ────────────────────────────────────────────────────────────────────────────
    private async Task<JsonObject> MapsAsync(string me, JsonObject body)
    {
        var redis = Redis();
        string lobbyId = ArgString(body["MatchID"]);
        var slugs = body["MapSlugs"] as JsonArray ?? throw new MissingFieldException("MapSlugs");
        var result = await EvalAsync(redis, "update_enabled_maps", [LobbyKey(lobbyId)], [me, .. slugs.Select(m => (RedisValue)ArgString(m))]);
        JsonNode? maps = null;
        if (!result.IsNull)
        {
            maps = Parse(result);
            await SendToLobbyAsync(redis, lobbyId, Notice(new JsonObject { ["template_id"] = "MapsSetForCustomGame", ["MatchID"] = lobbyId, ["Maps"] = maps?.DeepClone() }, lobbyId));
        }

        return LobbyDocuments.Ssc(Echo(body, "MatchID", new JsonObject { ["Maps"] = maps }));
    }

    // ── set_player_handicap_for_custom_game ─────────────────────────────────────────────────────────────────────────
    private async Task<JsonObject> HandicapAsync(string me, JsonObject body)
    {
        var redis = Redis();
        string lobbyId = ArgString(body["MatchID"]);
        JsonNode? handicaps = null;
        if (body["PlayerId"] is JsonValue p && p.TryGetValue<string>(out var playerId) && playerId == me)
        {
            var result = await EvalAsync(redis, "update_handicap", [LobbyKey(lobbyId)], [me, Js.Stringify(Required(body, "PlayerHandicap"))]);
            if (!result.IsNull && Parse(result) is JsonObject lobby)
            {
                handicaps = lobby["Handicaps"];
                await SendAsync(redis, Ids(lobby, "PlayerGameplayPreferences"), [me], Notice(new JsonObject
                {
                    ["MatchID"] = lobbyId,
                    ["template_id"] = "PlayerHandicapSetForCustomGame",
                    ["Handicaps"] = handicaps?.DeepClone(),
                }, lobbyId));
            }
        }

        return LobbyDocuments.Ssc(Echo(body, "MatchID", new JsonObject { ["Handicaps"] = handicaps?.DeepClone() }));
    }

    // ── switch_custom_game_lobby_team ───────────────────────────────────────────────────────────────────────────────
    private async Task<JsonObject> SwitchTeamAsync(string me, JsonObject body)
    {
        var redis = Redis();
        string lobbyId = ArgString(body["MatchID"]);
        var teamIndex = Required(body, "TeamIndex");
        var result = await EvalAsync(redis, "switch_team", [LobbyKey(lobbyId)], [me, ArgString(teamIndex)]);
        if (result.IsNull)
        {
            // Nothing to do (no such lobby, player or team, or no room): the TS server answered {} with no envelope.
            return [];
        }

        var moved = Parse(result) as JsonObject ?? throw new JsonException("switch_team returned nothing");
        await RefreshBuffsAsync(redis, lobbyId, Str(moved, "gameModeSlug"));
        await SendToLobbyAsync(redis, lobbyId, Notice(new JsonObject
        {
            ["template_id"] = "PlayerSwitchedCustomLobbyTeams",
            ["MatchID"] = lobbyId,
            ["Player"] = moved["playerData"]?.DeepClone(),
            ["TeamIndex"] = teamIndex.DeepClone(),
        }, lobbyId));
        return LobbyDocuments.Ssc(Echo(body, "MatchID", new JsonObject { ["Player"] = moved["playerData"]?.DeepClone(), ["TeamIndex"] = teamIndex.DeepClone() }));
    }

    // ── add_custom_game_bot ─────────────────────────────────────────────────────────────────────────────────────────
    private async Task<JsonObject> AddBotAsync(string me, JsonObject body)
    {
        var redis = Redis();
        string lobbyId = ArgString(body["MatchID"]);
        string botId = ArgString(body["BotAccountID"]);
        var teamIndex = Required(body, "TeamIndex");
        var bot = new JsonObject
        {
            ["Account"] = new JsonObject { ["id"] = botId },
            ["AccountID"] = botId,
            ["BotSettingSlug"] = body["BotSettingSlug"]?.DeepClone(),
            ["Fighter"] = AssetRef(body, "CharacterAssetPath", "CharacterSlug"),
            ["Skin"] = AssetRef(body, "SkinAssetPath", "SkinSlug"),
            ["JoinedAt"] = IsoNow(),
            ["CrossplayPreference"] = 0,
        };
        if (body["BotSettingSlug"] is null)
        {
            bot.Remove("BotSettingSlug");
        }

        var result = await EvalAsync(redis, "add_custom_game_bot", [LobbyKey(lobbyId)], [me, botId, ArgString(teamIndex), Js.Stringify(bot)]);
        JsonNode? added = null;
        if (!result.IsNull && Parse(result) is JsonObject made)
        {
            added = made["playerData"];
            await RefreshBuffsAsync(redis, lobbyId, Str(made, "gameModeSlug"));
            await SendToLobbyAsync(redis, lobbyId, Notice(new JsonObject
            {
                ["MatchID"] = lobbyId,
                ["template_id"] = "BotAddedToCustomGame",
                ["TeamIndex"] = teamIndex.DeepClone(),
                ["Bot"] = added?.DeepClone(),
            }, lobbyId));
        }

        return LobbyDocuments.Ssc(Echo(body, "MatchID", new JsonObject { ["Bot"] = added?.DeepClone(), ["TeamIndex"] = teamIndex.DeepClone() }), added is null ? 1 : 0);
    }

    // ── update_custom_game_bot_fighter ──────────────────────────────────────────────────────────────────────────────
    private async Task<JsonObject> BotFighterAsync(string me, JsonObject body)
    {
        var redis = Redis();
        string lobbyId = ArgString(body["MatchID"]);
        var result = await EvalAsync(redis, "update_bot_fighter", [LobbyKey(lobbyId)],
            [me, ArgString(body["BotAccountID"]), Js.Stringify(AssetRef(body, "CharacterAssetPath", "CharacterSlug")), Js.Stringify(AssetRef(body, "SkinAssetPath", "SkinSlug")), ArgString(body["BotSettingSlug"])]);
        JsonNode? bot = null;
        if (!result.IsNull)
        {
            bot = Parse(result);
            await SendToLobbyAsync(redis, lobbyId, Notice(new JsonObject { ["MatchID"] = lobbyId, ["template_id"] = "BotSettingsUpdatedForCustomGame", ["Bot"] = bot?.DeepClone() }, lobbyId));
        }

        return LobbyDocuments.Ssc(Echo(body, "MatchID", new JsonObject { ["Bot"] = bot?.DeepClone() }), bot is null ? 1 : 0);
    }

    // ── reset_custom_lobby_to_defaults ──────────────────────────────────────────────────────────────────────────────
    private async Task<JsonObject> ResetAsync(string me, JsonObject body)
    {
        var redis = Redis();
        string lobbyId = ArgString(body["MatchID"]);
        var stored = await GetLobbyAsync(redis, lobbyId);
        var lobby = Str(stored, "GameModeSlug") is { Length: > 0 } slug ? await ApplyGameSettingsAsync(redis, lobbyId, me, slug) : null;
        if (lobby is null)
        {
            return LobbyDocuments.Ssc(new JsonObject { ["error"] = "Not found or not leader" }, 1);
        }

        var data = new JsonObject
        {
            ["MatchID"] = lobbyId,
            ["GameModeSlug"] = lobby["GameModeSlug"]?.DeepClone(),
            ["MatchConfig"] = lobby["match_config"]?.DeepClone(),
            ["Maps"] = lobby["Maps"]?.DeepClone(),
        };
        var notice = new JsonObject { ["template_id"] = "CustomGameResetToDefaults" };
        foreach (var (key, value) in data)
        {
            notice[key] = value?.DeepClone();
        }

        await SendToLobbyAsync(redis, lobbyId, Notice(notice, lobbyId));
        return LobbyDocuments.Ssc(data);
    }

    // ── promote_to_lobby_leader ─────────────────────────────────────────────────────────────────────────────────────
    private async Task<JsonObject> PromoteAsync(string me, JsonObject body)
    {
        var redis = Redis();
        string lobbyId = ArgString(body["MatchID"]);
        string target = ArgString(body["PromoteTarget"]);
        var result = await EvalAsync(redis, "promote_leader", [LobbyKey(lobbyId)], [target, me]);
        if (result.IsNull || Parse(result) is not JsonObject lobby)
        {
            return new JsonObject { ["body"] = null, ["metadata"] = null, ["return_code"] = 1 };
        }

        await SendAsync(redis, Ids(lobby, "PlayerGameplayPreferences"), [me], Notice(new JsonObject
        {
            ["MatchID"] = lobbyId,
            ["template_id"] = "LobbyLeaderChanged",
            ["LeaderID"] = target,
            ["ReadyPlayers"] = lobby["ReadyPlayers"]?.DeepClone(),
        }, lobbyId));
        return LobbyDocuments.Ssc(new JsonObject { ["MatchID"] = lobbyId, ["LeaderID"] = target, ["ReadyPlayers"] = lobby["ReadyPlayers"]?.DeepClone() });
    }

    // ── kick_from_lobby ─────────────────────────────────────────────────────────────────────────────────────────────
    private async Task<JsonObject> KickAsync(string me, JsonObject body)
    {
        var redis = Redis();
        string lobbyId = ArgString(body["MatchID"]);
        string kickee = ArgString(body["KickeeAccountID"]);
        // Everyone before the kick, the kicked player included.
        var before = await GetLobbyAsync(redis, lobbyId);
        var everyone = before is null ? [] : Ids(before, "PlayerGameplayPreferences");
        var result = await EvalAsync(redis, "kick_from_lobby", [LobbyKey(lobbyId)], [me, kickee]);
        JsonNode? player = null;
        if (!result.IsNull && Parse(result) is JsonObject kicked)
        {
            player = kicked["playerData"];
            await PlayerMessages.SendAsync(redis, everyone, Notice(new JsonObject
            {
                ["MatchID"] = lobbyId,
                ["template_id"] = "PlayerKickedFromLobby",
                ["Player"] = player?.DeepClone(),
                ["KickeeAccountID"] = kickee,
            }, lobbyId));
            await RefreshBuffsAsync(redis, lobbyId, Str(kicked, "gameModeSlug"));
            await redis.KeyDeleteAsync(PlayerKey(kickee));
        }

        return LobbyDocuments.Ssc(Echo(body, "MatchID", new JsonObject { ["Player"] = player?.DeepClone() }), player is null ? 1 : 0);
    }

    // ── set_world_buffs_for_custom_game ─────────────────────────────────────────────────────────────────────────────
    private async Task<JsonObject> WorldBuffsAsync(string me, JsonObject body)
    {
        var redis = Redis();
        string lobbyId = ArgString(body["MatchID"]);
        JsonArray? merged = null;
        if (await GetLobbyAsync(redis, lobbyId) is { } stored && Str(stored, "GameModeSlug") is { Length: > 0 } slug)
        {
            // The mode's required buffs first, then the asked ones, each once.
            var asked = body["WorldBuffSlugs"] as JsonArray ?? throw new MissingFieldException("WorldBuffSlugs");
            var seen = new HashSet<string>();
            merged = [];
            foreach (var buff in GameModes.WorldBuffs(slug).Concat(asked))
            {
                if (seen.Add(buff?.ToJsonString() ?? "null"))
                {
                    merged.Add(buff?.DeepClone());
                }
            }

            var result = await EvalAsync(redis, "set_world_buffs", [LobbyKey(lobbyId)], [me, Js.Stringify(merged)]);
            if (result.IsNull)
            {
                merged = null;
            }
            else
            {
                await SendToLobbyAsync(redis, lobbyId, Notice(new JsonObject { ["template_id"] = "WorldBuffsSetForCustomGame", ["MatchID"] = lobbyId, ["WorldBuffs"] = merged.DeepClone() }, lobbyId));
            }
        }

        return LobbyDocuments.Ssc(Echo(body, "MatchID", new JsonObject { ["WorldBuffs"] = merged }), merged is null ? 1 : 0);
    }

    // ── lobby_code ──────────────────────────────────────────────────────────────────────────────────────────────────
    private async Task<JsonObject> LobbyCodeAsync(string me, JsonObject body)
    {
        var redis = Redis();
        string lobbyId = ArgString(body["LobbyId"]);
        string? code = null;
        if (await GetLobbyAsync(redis, lobbyId) is { } lobby && Str(lobby, "LeaderID") == me)
        {
            for (int attempt = 0; attempt < 5 && code is null; attempt++)
            {
                string drawn = string.Concat(Enumerable.Range(0, 5).Select(_ => CodeAlphabet[Random.Shared.Next(CodeAlphabet.Length)]));
                if (await redis.StringSetAsync($"lobby_code:{drawn}", lobbyId, s_lobbyTtl, When.NotExists))
                {
                    code = drawn;
                }
            }

            if (code is not null)
            {
                await EvalAsync(redis, "set_lobby_code", [LobbyKey(lobbyId)], [code]);
            }
        }

        return LobbyDocuments.Ssc(new JsonObject { ["LobbyCode"] = code });
    }

    public async Task<JsonObject?> ByCodeAsync(string code, CancellationToken ct)
    {
        if (code.Length > 10 || services.GetService<IConnectionMultiplexer>()?.GetDatabase() is not { } redis)
        {
            return null;
        }

        if (await redis.StringGetAsync($"lobby_code:{code.ToUpperInvariant()}") is not { IsNullOrEmpty: false } id
            || await GetLobbyAsync(redis, id.ToString()) is not { } lobby)
        {
            return null;
        }

        // The TS server's dates were JS Dates, which its Hydra encoder writes as empty maps.
        return new JsonObject
        {
            ["updated_at"] = new JsonObject(),
            ["created_at"] = new JsonObject(),
            ["account_id"] = null,
            ["completion_time"] = null,
            ["name"] = "white-green-wind-breeze-OS5dF",
            ["state"] = "open",
            ["access_level"] = "public",
            ["origin"] = "client",
            ["rand"] = Random.Shared.NextDouble(),
            ["winning_team"] = new JsonArray(),
            ["win"] = new JsonArray(),
            ["loss"] = new JsonArray(),
            ["draw"] = null,
            ["arbitration"] = null,
            ["data"] = new JsonObject(),
            ["server_data"] = lobby,
            ["players"] = new JsonObject(),
            ["matchmaking"] = null,
            ["cluster"] = Cluster,
            ["last_warning_time"] = null,
            ["template"] = new JsonObject
            {
                ["type"] = "async",
                ["name"] = "custom_game_lobby",
                ["slug"] = "custom_game_lobby",
                ["min_players"] = 1,
                ["max_players"] = 8,
                ["game_server_integration_enabled"] = false,
                ["game_server_config"] = null,
                ["created_at"] = new JsonObject(),
                ["updated_at"] = new JsonObject(),
                ["data"] = new JsonObject(),
                ["id"] = "",
            },
            ["criteria"] = new JsonObject { ["slug"] = null },
            ["shortcode"] = null,
            ["id"] = id.ToString(),
            ["access"] = "public",
        };
    }

    // ── start_custom_match ──────────────────────────────────────────────────────────────────────────────────────────
    private async Task<JsonObject> StartAsync(string me, JsonObject body, CancellationToken ct) =>
        LobbyDocuments.Ssc([], await StartMatchAsync(me, ArgString(body["LobbyId"]), ct) ? 0 : 1);

    public async Task<bool> RematchAsync(string lobbyId, CancellationToken ct)
    {
        var redis = Redis();
        if (await GetLobbyAsync(redis, lobbyId) is not { } lobby || await HumansAsync(redis, lobbyId) is not { } humans)
        {
            log.LogWarning("No rematch in lobby {Lobby}: it is gone", lobbyId);
            return false;
        }

        if (humans.Playing.Count == 0)
        {
            log.LogWarning("No rematch in lobby {Lobby}: no player but bots is left in its teams", lobbyId);
            return false;
        }

        return await StartMatchAsync(Str(lobby, "LeaderID") ?? "", lobbyId, ct);
    }

    /// <summary>
    /// A match's end in the lobby (MatchEnd): every ready flag taken down, so starting the next one needs everyone ready
    /// again. False when the lobby is gone.
    /// </summary>
    internal static async Task<bool> MatchEndedAsync(IDatabase redis, string lobbyId) =>
        !(await EvalAsync(redis, "reset_ready", [LobbyKey(lobbyId)], [])).IsNull;

    /// <summary>
    /// The lobby's players (not bots): those in teams 0-3, who play (and vote on a rematch), and all of them, spectators
    /// (team 4) too. Null when there is no lobby.
    /// </summary>
    internal static async Task<(IReadOnlyList<string> Playing, IReadOnlyList<string> All)?> HumansAsync(IDatabase redis, string lobbyId)
    {
        if (await GetLobbyAsync(redis, lobbyId) is not { } lobby)
        {
            return null;
        }

        var playing = new List<string>();
        var all = new List<string>();
        foreach (var team in Teams(lobby))
        {
            foreach (var (id, player) in Players(team).Where(p => IsHuman(p.Value)))
            {
                all.Add(id);
                if (Int(team["TeamIndex"]) is >= 0 and <= 3)
                {
                    playing.Add(id);
                }
            }
        }

        return (playing, all);
    }

    // The match started by the lobby's leader (me): false when it was not.
    private async Task<bool> StartMatchAsync(string me, string lobbyId, CancellationToken ct)
    {
        var redis = Redis();
        var lobby = await GetLobbyAsync(redis, lobbyId);
        if (lobby is null || Str(lobby, "LeaderID") != me)
        {
            return false;
        }

        // Every player (not a bot) must be on a current client, spectators included.
        var teams = Teams(lobby).ToList();
        var humans = teams.SelectMany(t => Players(t).Where(p => IsHuman(p.Value)).Select(p => p.Key)).ToList();
        var outdated = await gate.RequiringUpdateAsync(humans);
        if (outdated.Count > 0)
        {
            await gate.RequestModalsAsync(outdated.Select(o => o.AccountId));
            log.LogWarning("Blocked custom match start in {Lobby}: update required for {Players}", lobbyId,
                string.Join(", ", outdated.Select(o => $"{o.AccountId}:{(o.ClientVersion.Length > 0 ? o.ClientVersion : "legacy")}")));
            return false;
        }

        var maps = lobby["Maps"] as JsonArray ?? [];
        var selected = maps.Where(m => Truthy(m?["IsSelected"])).ToList();
        var picked = selected.Count > 0 ? selected[Random.Shared.Next(selected.Count)] : maps.FirstOrDefault();
        string map = picked?["Map"] is JsonValue mv && mv.TryGetValue<string>(out var ms) ? ms : "M003_V1";

        // Who plays (teams 0-3, bots included) and who watches (team 4), with each one's buffs and address. A player
        // missing from PlayerGameplayPreferences has no settings and is left out, as there.
        var members = Ids(lobby, "PlayerGameplayPreferences").ToHashSet();
        var buffsOf = lobby["PlayerBuffs"] as JsonObject;
        var playing = new Dictionary<string, (JsonArray Buffs, string Ip)>();
        var watching = new Dictionary<string, (JsonArray Buffs, string Ip)>();
        var buffMap = new JsonObject();
        foreach (var team in teams)
        {
            bool spectators = Int(team["TeamIndex"]) == 4;
            foreach (var (id, player) in Players(team))
            {
                var buffs = buffsOf?[id] as JsonArray ?? [];
                string ip = "";
                if (IsHuman(player))
                {
                    if (!members.Contains(id))
                    {
                        continue;
                    }

                    var known = await PlayerConfigAsync(redis, id);
                    ip = known.Ip;
                    // The loadout: player:{id}, then the session, then the lobby's locked one, then the settings read before.
                    var fresh = await HashAsync(redis, $"player:{id}");
                    var session = await HashAsync(redis, $"connections:{id}");
                    var locked = lobby["LockedLoadouts"]?[id];
                    string character = Or(Get(fresh, "character"), Get(session, "character"), Str(locked as JsonObject, "Character"), known.Character);
                    string skin = Or(Get(fresh, "skin"), Get(session, "skin"), Str(locked as JsonObject, "Skin"), known.Skin);
                    if (character.Length > 0 && skin.Length > 0)
                    {
                        await PlayedLoadout.RecordAsync(redis, id, character, skin);
                    }
                }

                (spectators ? watching : playing)[id] = (buffs, ip);
                if (!spectators && buffs.Count > 0)
                {
                    buffMap[id] = buffs.DeepClone();
                }
            }
        }

        // Player indexes as the TS server gives them (the rollback server depends on these): within a team, players
        // before bots, index = place in team * 2 + team; the first player (not a bot) of the walk hosts; spectators
        // 8888, 8889, ... on team -1.
        var entries = new List<MatchPlayer>();
        var spectatorEntries = new List<MatchPlayer>();
        int humansSeen = 0;
        foreach (var team in teams)
        {
            int teamIndex = Int(team["TeamIndex"]);
            if (teamIndex == 4)
            {
                int next = 0;
                foreach (var (id, player) in Players(team))
                {
                    if (!IsHuman(player))
                    {
                        continue;
                    }

                    string ip = playing.TryGetValue(id, out var p) ? p.Ip : watching.TryGetValue(id, out var w) ? w.Ip : "";
                    spectatorEntries.Add(new MatchPlayer(id, 8888 + next++, -1, false, ip, false, IsSpectator: true));
                }
            }
            else if (teamIndex is >= 0 and <= 3)
            {
                int place = 0;
                foreach (var (id, player) in Players(team).OrderBy(p => IsHuman(p.Value) ? 0 : 1))
                {
                    if (!playing.TryGetValue(id, out var entry))
                    {
                        continue;
                    }

                    bool isBot = !IsHuman(player);
                    entries.Add(new MatchPlayer(id, place * 2 + teamIndex, teamIndex, !isBot && humansSeen == 0, entry.Ip, isBot));
                    place++;
                    if (!isBot)
                    {
                        humansSeen++;
                        continue;
                    }

                    // The websocket builds a bot's match config from this (bots have no session or record).
                    var bot = player as JsonObject;
                    int difficulty = BotDefaults.Difficulty.TryGetValue(Str(bot, "BotSettingSlug") ?? "", out int d) ? d : BotDefaults.Difficulty["Medium"];
                    await redis.HashSetAsync($"bot_config:{id}",
                    [
                        new("character", Str(bot?["Fighter"] as JsonObject, "Slug") ?? "character_jason"),
                        new("skin", Str(bot?["Skin"] as JsonObject, "Slug") ?? "skin_jason_000"),
                        new("difficultyMin", difficulty.ToString(CultureInfo.InvariantCulture)),
                        new("difficultyMax", difficulty.ToString(CultureInfo.InvariantCulture)),
                        new("settingSlug", Str(bot, "BotSettingSlug") ?? ""),
                    ]);
                    await redis.KeyExpireAsync($"bot_config:{id}", TimeSpan.FromDays(1));
                }
            }
        }

        var config = lobby["match_config"] as JsonObject ?? [];
        string style = Str(config, "TeamStyle") ?? "";
        string mode = style == "Solos" ? "1v1" : style == "FFA" ? "FFA" : "2v2";
        var fields = new JsonObject { ["isCustomGame"] = true };
        CopyIfPresent(config, "NumRingoutsForWin", fields, "customNumRingouts");
        CopyIfPresent(config, "MatchDuration", fields, "customMatchTime");
        CopyIfPresent(config, "AllowHazards", fields, "customHazards");
        fields["customShields"] = Truthy(config["EnableShields"]);
        fields["worldBuffs"] = lobby["WorldBuffs"]?.DeepClone();
        fields["playerBuffs"] = buffMap;

        var all = entries.Concat(spectatorEntries).ToList();
        var launched = await launcher.LaunchAsync(new MatchLaunch(mode, map, mode, all,
            BotPerks: BotDefaults.PerksArray(), NotificationFields: fields), ct);
        if (launched is null)
        {
            return false;
        }

        // For the match end and the rematch (MatchEnd, Rematches).
        await redis.StringSetAsync($"ssc_custom_lobby_match:{launched.MatchId}", lobbyId, s_matchTtl);
        foreach (var player in all)
        {
            await redis.StringSetAsync(PlayerKey(player.PlayerId), lobbyId, s_matchTtl);
        }

        log.LogInformation("Custom match {Match} started from lobby {Lobby} on map {Map} with {Count} players", launched.MatchId, lobbyId, map, all.Count);
        return true;
    }

    // ── The shared routes' custom lobby side ────────────────────────────────────────────────────────────────────────

    /// <summary>leave_player_lobby: out of the lobby; answered a fresh solo lobby (not saved: the game makes one next).</summary>
    private async Task<JsonObject?> LeaveAsync(string lobbyId, string me)
    {
        var redis = Redis();
        var lobby = await GetLobbyAsync(redis, lobbyId);
        if (lobby is null)
        {
            return null;
        }

        await redis.KeyDeleteAsync(PlayerKey(me));
        var solo = await BaseLobbyAsync(redis, me);
        solo["ModeString"] = "1v1";
        if (lobby["match_config"] is JsonObject)
        {
            var result = await EvalAsync(redis, "leave_custom_lobby", [LobbyKey(lobbyId)], [me]);
            if (!result.IsNull && Parse(result) is JsonObject left)
            {
                if (Str(left, "gameModeSlug") is { Length: > 0 } slug)
                {
                    await RefreshBuffsAsync(redis, lobbyId, slug);
                }

                await SendToLobbyAsync(redis, lobbyId, Notice(new JsonObject
                {
                    ["MatchID"] = lobbyId,
                    ["template_id"] = "PlayerLeftLobby",
                    ["Player"] = left["playerData"]?.DeepClone(),
                    ["ReadyPlayers"] = left["readyPlayers"]?.DeepClone(),
                    ["NewLeader"] = left["leaderID"]?.DeepClone(),
                }, lobbyId));
            }
        }
        else
        {
            await redis.KeyDeleteAsync(LobbyKey(lobbyId));
        }

        return solo;
    }

    /// <summary>invite_to_player_lobby: the invite, to the invited player.</summary>
    private async Task<JsonObject> InviteAsync(string lobbyId, string me, JsonObject body)
    {
        var redis = Redis();
        bool spectator = Truthy(body["IsSpectator"]);
        var isSpectator = body["IsSpectator"]?.DeepClone() ?? false;
        if (await GetLobbyAsync(redis, lobbyId) is not null && body["InviteeAccountID"] is JsonValue v && v.TryGetValue<string>(out var invitee))
        {
            await PlayerMessages.SendAsync(redis, [invitee], new JsonObject
            {
                ["data"] = new JsonObject
                {
                    ["LobbyType"] = 0,
                    ["MatchID"] = lobbyId,
                    ["ContextData"] = new JsonObject { ["LobbyType"] = "Custom" },
                    ["template_id"] = "InviteReceivedForLobby",
                    ["IsSpectator"] = isSpectator.DeepClone(),
                    ["InviterAccountId"] = me,
                },
                ["payload"] = new JsonObject
                {
                    ["frm"] = new JsonObject { ["id"] = "internal-server", ["type"] = "server-api-key" },
                    ["template"] = "realtime",
                    ["account_id"] = invitee,
                    ["profile_id"] = invitee,
                },
                ["header"] = "",
                ["cmd"] = "profile-notification",
            });
        }

        log.LogInformation("Custom lobby invite from {Player} for lobby {Lobby} (spectator {Spectator})", me, lobbyId, spectator);
        return LobbyDocuments.Ssc(new JsonObject { ["MatchID"] = lobbyId, ["IsSpectator"] = isSpectator });
    }

    /// <summary>lock_lobby_loadout: the player's character and skin, in the lobby and their loadout; null without both.</summary>
    private async Task<JsonObject?> LockLoadoutAsync(string lobbyId, string me, JsonObject body)
    {
        var redis = Redis();
        if (body["Loadout"]?["Character"] is not JsonValue c || !c.TryGetValue<string>(out var character)
            || body["Loadout"]?["Skin"] is not JsonValue s || !s.TryGetValue<string>(out var skin))
        {
            // The TS server failed writing the loadout and fell through to the party route.
            return null;
        }

        var lobby = await GetLobbyAsync(redis, lobbyId);
        if (lobby is not null)
        {
            await EvalAsync(redis, "lock_loadout", [LobbyKey(lobbyId)], [me, character, skin]);
            await redis.HashSetAsync($"player:{me}", [new("character", character), new("skin", skin)]);
            await SendAsync(redis, Ids(lobby, "PlayerGameplayPreferences"), [me], new JsonObject
            {
                ["data"] = new JsonObject
                {
                    ["Loadout"] = LobbyDocuments.Loadout(character, skin),
                    ["AccountId"] = me,
                    ["LobbyId"] = lobbyId,
                    ["template_id"] = "OnPlayerLoadoutLocked",
                    ["bAreAllLoadoutsLocked"] = true,
                },
                ["payload"] = new JsonObject { ["match"] = new JsonObject { ["id"] = lobbyId }, ["custom_notification"] = "realtime" },
                ["header"] = "",
                ["cmd"] = "update",
            });
        }

        return LobbyDocuments.Ssc(new JsonObject { ["AccountId"] = me, ["Loadout"] = LobbyDocuments.Loadout(character, skin), ["bAreAllLoadoutsLocked"] = true });
    }

    /// <summary>set_ready_for_lobby: the player's ready flag; the answer's body (null when the lobby is gone).</summary>
    private async Task<JsonObject?> SetReadyAsync(string lobbyId, string me, JsonObject body)
    {
        var redis = Redis();
        var result = await EvalAsync(redis, "set_player_ready", [LobbyKey(lobbyId)], [me, Truthy(body["Ready"]) ? "true" : "false"]);
        if (await GetLobbyAsync(redis, lobbyId) is null)
        {
            return null;
        }

        var data = new JsonObject { ["MatchID"] = lobbyId, ["PlayerID"] = me };
        if (body["Ready"] is { } ready)
        {
            data["Ready"] = ready.DeepClone();
        }

        data["bAllPlayersReady"] = result.Resp2Type == ResultType.Integer && (long)result == 1;
        var notice = new JsonObject { ["template_id"] = "PlayerReadyForLobby" };
        foreach (var (key, value) in data)
        {
            notice[key] = value?.DeepClone();
        }

        await SendToLobbyAsync(redis, lobbyId, Notice(notice, lobbyId));
        return data;
    }

    // ── Lobbies ─────────────────────────────────────────────────────────────────────────────────────────────────────

    private static string LobbyKey(string lobbyId) => $"custom_lobby_ssc:{lobbyId}";

    private static string PlayerKey(string playerId) => $"ssc_custom_lobby_player:{playerId}";

    /// <summary>
    /// A lobby with only <paramref name="me"/> in it (createBaseLobby): their settings, a new id; keys in the TS order.
    /// </summary>
    private async Task<JsonObject> BaseLobbyAsync(IDatabase redis, string me)
    {
        var config = await PlayerConfigAsync(redis, me);
        var teams = new JsonArray(new JsonObject
        {
            ["TeamIndex"] = 0,
            ["Players"] = new JsonObject
            {
                [me] = new JsonObject
                {
                    ["Account"] = new JsonObject { ["id"] = me },
                    ["JoinedAt"] = IsoNow(),
                    ["BotSettingSlug"] = "",
                    ["LobbyPlayerIndex"] = 0,
                    ["CrossplayPreference"] = 1,
                },
            },
            ["Length"] = 1,
        });
        for (int t = 1; t <= 4; t++)
        {
            teams.Add(new JsonObject { ["TeamIndex"] = t, ["Players"] = new JsonObject(), ["Length"] = 0 });
        }

        return new JsonObject
        {
            ["Teams"] = teams,
            ["LeaderID"] = me,
            ["LobbyType"] = 0,
            ["ReadyPlayers"] = new JsonObject(),
            ["PlayerGameplayPreferences"] = new JsonObject { [me] = config.Preferences },
            ["PlayerAutoPartyPreferences"] = new JsonObject { [me] = false },
            ["GameVersion"] = lobbySettings.CurrentValue.GameVersion,
            ["HissCrc"] = 1167552915,
            ["Platforms"] = new JsonObject { [me] = "PC" },
            ["AllMultiplayParams"] = LobbyDocuments.AllMultiplay(),
            ["LockedLoadouts"] = new JsonObject { [me] = LobbyDocuments.Loadout(config.Character, config.Skin) },
            ["IsLobbyJoinable"] = true,
            ["MatchID"] = ObjectId.GenerateNewId().ToString(),
        };
    }

    /// <summary>A lobby as stored, repaired (getLobby); null when there is none or it is not JSON.</summary>
    private static async Task<JsonObject?> GetLobbyAsync(IDatabase redis, string lobbyId)
    {
        var raw = await redis.StringGetAsync(LobbyKey(lobbyId));
        if (raw.IsNullOrEmpty)
        {
            return null;
        }

        try
        {
            return FixEmptyTables(Js.Parse(raw.ToString())) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A mode's settings applied to the lobby by its leader (applyGameSettings): the lobby after, or null.</summary>
    private static async Task<JsonObject?> ApplyGameSettingsAsync(IDatabase redis, string lobbyId, string leader, string slug)
    {
        var defaults = GameModes.DefaultSettings(slug);
        var matrix = GameModes.BuffMatrix(slug);
        var result = await EvalAsync(redis, "apply_game_settings", [LobbyKey(lobbyId)],
            [leader, slug, Js.Stringify(defaults["match_config"]), Js.Stringify(defaults["Maps"]), Js.Stringify(defaults["WorldBuffs"]), Js.Stringify(matrix)]);
        return result.IsNull ? null : Parse(result) as JsonObject;
    }

    /// <summary>Each player's buffs for the lobby's mode, again (refreshPlayerBuffs).</summary>
    private static Task RefreshBuffsAsync(IDatabase redis, string lobbyId, string? slug)
    {
        if (slug is null)
        {
            throw new UnknownGameModeException("undefined", "is not a game mode");
        }

        return EvalAsync(redis, "refresh_player_buffs", [LobbyKey(lobbyId)], [Js.Stringify(GameModes.BuffMatrix(slug))]);
    }

    private static async Task<RedisResult> EvalAsync(IDatabase redis, string script, RedisKey[] keys, RedisValue[] args) =>
        await redis.ScriptEvaluateAsync(s_scripts.Value.TryGetValue(script, out var text) ? text : throw new InvalidOperationException($"no script {script}"), keys, args);

    /// <summary>A script's JSON, parsed as JSON.parse would and repaired.</summary>
    private static JsonNode? Parse(RedisResult result) => FixEmptyTables(Js.Parse((string)result!));

    /// <summary>
    /// cjson's empty tables put back (fixCjsonEmptyTables): an empty object or array is an object under the keys that
    /// hold maps, an array under those that hold lists, and left alone elsewhere.
    /// </summary>
    internal static JsonNode? FixEmptyTables(JsonNode? node)
    {
        switch (node)
        {
            case JsonArray array:
                for (int i = 0; i < array.Count; i++)
                {
                    var item = array[i];
                    if (item is JsonObject or JsonArray)
                    {
                        array[i] = FixEmptyTables(item?.DeepClone());
                    }
                }

                return array;
            case JsonObject obj:
                foreach (var key in obj.Select(kv => kv.Key).ToList())
                {
                    var value = obj[key];
                    bool empty = value is JsonObject { Count: 0 } or JsonArray { Count: 0 };
                    if (empty && s_objectFields.Contains(key))
                    {
                        obj[key] = new JsonObject();
                    }
                    else if (empty && s_arrayFields.Contains(key))
                    {
                        obj[key] = new JsonArray();
                    }
                    else if (!empty && value is JsonObject or JsonArray)
                    {
                        obj[key] = FixEmptyTables(value.DeepClone());
                    }
                }

                return obj;
            default:
                return node;
        }
    }

    private static IEnumerable<JsonObject> Teams(JsonObject lobby) => (lobby["Teams"] as JsonArray ?? []).OfType<JsonObject>();

    private static JsonObject Players(JsonObject team) => team["Players"] as JsonObject ?? [];

    // A player, not a bot: BotSettingSlug is exactly "" (the TS server's test both ways).
    private static bool IsHuman(JsonNode? player) => player?["BotSettingSlug"] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length == 0;

    /// <summary>The keys of the lobby's map <paramref name="field"/> (Object.keys): the players in it, for PlayerGameplayPreferences.</summary>
    private static List<string> Ids(JsonObject lobby, string field) =>
        lobby[field] is JsonObject map ? [.. map.Select(kv => kv.Key)] : throw new InvalidOperationException($"the lobby has no {field}");

    // ── Messages ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A lobby update: {data, payload: {custom_notification: "realtime", match: {id}}, cmd: "update", header: ""}.</summary>
    private static JsonObject Notice(JsonObject data, string lobbyId) => new()
    {
        ["data"] = data,
        ["payload"] = new JsonObject { ["custom_notification"] = "realtime", ["match"] = new JsonObject { ["id"] = lobbyId } },
        ["cmd"] = "update",
        ["header"] = "",
    };

    /// <summary>To <paramref name="targets"/> but <paramref name="exclude"/> (broadcastToUsers).</summary>
    private static Task SendAsync(IDatabase redis, IEnumerable<string> targets, IReadOnlyCollection<string> exclude, JsonObject message) =>
        PlayerMessages.SendAsync(redis, targets.Where(t => !exclude.Contains(t)), message);

    /// <summary>To everyone in the lobby as it is now (broadcastToTopic): nobody when it is gone.</summary>
    private static async Task SendToLobbyAsync(IDatabase redis, string lobbyId, JsonObject message)
    {
        if (await GetLobbyAsync(redis, lobbyId) is { } lobby)
        {
            await PlayerMessages.SendAsync(redis, Ids(lobby, "PlayerGameplayPreferences"), message);
        }
    }

    // ── Players ─────────────────────────────────────────────────────────────────────────────────────────────────────

    private sealed record PlayerConfig(string Character, string Skin, long Preferences, string Ip);

    /// <summary>What the lobby takes from a player's record and session (getPlayerConfig).</summary>
    private static async Task<PlayerConfig> PlayerConfigAsync(IDatabase redis, string id)
    {
        var player = await HashAsync(redis, $"player:{id}");
        var session = await HashAsync(redis, $"connections:{id}");
        return new PlayerConfig(
            Or(Get(player, "character"), Get(session, "character")),
            Or(Get(player, "skin"), Get(session, "skin")),
            GameplayPreferences.Of(Get(session, "GameplayPreferences")),
            Or(Get(session, "current_ip")));
    }

    private static async Task<Dictionary<string, string>> HashAsync(IDatabase redis, string key) =>
        (await redis.HashGetAllAsync(key)).ToDictionary(e => e.Name.ToString(), e => e.Value.ToString());

    // ── Values ──────────────────────────────────────────────────────────────────────────────────────────────────────

    private string IsoNow() => time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static string? Get(Dictionary<string, string> hash, string field) => hash.TryGetValue(field, out var value) ? value : null;

    private static string? Str(JsonObject? obj, string key) => obj?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static string Or(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? "";

    private static int Int(JsonNode? node) => node is JsonValue v && v.TryGetValue<double>(out var d) ? (int)d : -1;

    /// <summary>A value the TS server passed to a script as text (String(x), x.toString()): absent or null threw there.</summary>
    private static string ArgString(JsonNode? node) => node switch
    {
        null => throw new MissingFieldException("a request field is missing"),
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.GetValueKind() is JsonValueKind.True or JsonValueKind.False => v.GetValueKind() == JsonValueKind.True ? "true" : "false",
        JsonValue v when v.GetValueKind() == JsonValueKind.Number => JsNumber(double.Parse(v.ToJsonString(), CultureInfo.InvariantCulture)),
        _ => throw new MissingFieldException("a request field is not a value"),
    };

    // A number as JavaScript writes it, for the values a lobby request carries (integers, mostly).
    private static string JsNumber(double d) => d == Math.Floor(d) && Math.Abs(d) < 1e21 ? ((decimal)d).ToString(CultureInfo.InvariantCulture) : d.ToString("R", CultureInfo.InvariantCulture);

    private static JsonNode Required(JsonObject body, string key) => body[key] ?? throw new MissingFieldException(key);

    /// <summary>JavaScript truthiness of a request value.</summary>
    private static bool Truthy(JsonNode? node) => node switch
    {
        null => false,
        JsonValue v when v.GetValueKind() == JsonValueKind.True => true,
        JsonValue v when v.GetValueKind() == JsonValueKind.False => false,
        JsonValue v when v.GetValueKind() == JsonValueKind.Number => double.Parse(v.ToJsonString(), CultureInfo.InvariantCulture) is var d && d != 0 && !double.IsNaN(d),
        JsonValue v when v.TryGetValue<string>(out var s) => s.Length > 0,
        _ => true,
    };

    /// <summary>{AssetPath, Slug} from two request fields, a missing one left out (JSON.stringify of undefined).</summary>
    private static JsonObject AssetRef(JsonObject body, string pathKey, string slugKey)
    {
        var asset = new JsonObject();
        if (body[pathKey] is { } path)
        {
            asset["AssetPath"] = path.DeepClone();
        }

        if (body[slugKey] is { } slug)
        {
            asset["Slug"] = slug.DeepClone();
        }

        return asset;
    }

    /// <summary>{<paramref name="key"/>: the request's, then <paramref name="rest"/>}; the request's left out when it had none.</summary>
    private static JsonObject Echo(JsonObject body, string key, JsonObject rest)
    {
        var answer = new JsonObject();
        if (body[key] is { } value)
        {
            answer[key] = value.DeepClone();
        }

        foreach (var (k, v) in rest.ToList())
        {
            rest.Remove(k);
            answer[k] = v;
        }

        return answer;
    }

    private static void CopyIfPresent(JsonObject from, string key, JsonObject to, string asKey)
    {
        if (from[key] is { } value)
        {
            to[asKey] = value.DeepClone();
        }
    }
}

/// <summary>A request field a custom lobby route needs and did not get (the TS server threw on it).</summary>
public sealed class MissingFieldException(string field) : Exception($"{field} is missing");

public static class CustomLobbyHosting
{
    public static WebApplicationBuilder AddCustomLobbies(this WebApplicationBuilder builder)
    {
        builder.AddSetting<CustomLobbySettings>("CustomLobbies");
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ICustomLobbyService, CustomLobbyService>();
        return builder;
    }
}

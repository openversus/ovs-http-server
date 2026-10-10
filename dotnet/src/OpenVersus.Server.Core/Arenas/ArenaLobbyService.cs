using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Lobbies;
using OpenVersus.Server.Core.Matches;
using OpenVersus.Server.Core.Preferences;
using OpenVersus.Server.Core.Realtime;
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
//                 connections:{player} lobby_id = id
// A party goes into the Arena together (the lobby rule for every kind): the game of the player who presses Arena sends only
// create_arena_lobby, so the others in their party lobby are sent an Arena invite (LobbyInvites), which their game asks
// them to accept ("Accept arena invite from {Username}?") and answers with join_arena_lobby.
//
// PUT /ssc/invoke/join_arena_lobby: an accepted Arena invite (bench, 2026-10-10). The lobby is the one the invite named
// (pending_join_lobby), else the one the body names. The player joins on the first team with room (ArenaLobby.Join), is
// answered the lobby, and everyone already in it is told (PlayerJoinedLobby). Refused (return_code 1): no Arena lobby
// there, or it is full.
// Redis, written  lobby:{id} (playerIds, teams; 8 h from two players); player_lobby:{player} = id EX 8 h;
//                 connections:{player} lobby_id = id; pending_join_lobby:{player} deleted
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

    /// <summary>The join_arena_lobby answer for the player the session token names.</summary>
    Task<JsonObject> JoinAsync(JsonObject? claims, JsonObject? request, CancellationToken ct);

    /// <summary>switch_custom_game_lobby_team in an Arena lobby (the request's MatchID), else null: another lobby's.</summary>
    Task<JsonObject?> SwitchTeamAsync(string playerId, JsonObject? request, CancellationToken ct);

    /// <summary>kick_from_lobby from an Arena lobby (the request's LobbyId or MatchID), else null: another lobby's.</summary>
    Task<JsonObject?> KickAsync(string playerId, JsonObject? request, CancellationToken ct);

    /// <summary>
    /// <paramref name="playerId"/> leaves Arena lobby <paramref name="lobbyId"/>: the party lobby they go back to, or null
    /// when they have none (then a solo party lobby is theirs, as for any leave). A voluntary leave (leave_player_lobby,
    /// <paramref name="bringPartners"/>) invites the partner they came with back too.
    /// </summary>
    Task<string?> LeaveAsync(string playerId, string lobbyId, bool bringPartners = true);

    /// <summary>
    /// The lobby_code answer when <paramref name="request"/>'s LobbyId is an Arena lobby ({LobbyCode}, null for someone not
    /// in it), else null: the request is another lobby's.
    /// </summary>
    Task<JsonObject?> LobbyCodeAsync(string playerId, JsonObject? request, CancellationToken ct);
}

internal sealed class ArenaLobbyService(IServiceProvider services, IOptionsMonitor<LobbySettings> lobbies, TimeProvider time,
    ILogger<ArenaLobbyService> log) : IArenaLobbyService
{
    public const string Mode = ArenaLobby.TemplateName;

    /// <summary>The lobby's teams: eight of two (WB: "8 teams of 2").</summary>
    public const int TeamCount = ArenaLobby.TeamCount;

    // The delay before the others are told of a join, after its answer (the party join's): the game handles the answer first.
    internal static TimeSpan JoinNoticeDelay = TimeSpan.FromMilliseconds(500);

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
        // The party they come from (read before the pointer moves to the Arena lobby): where a leave takes them back.
        var from = await LobbyStore.PointerAsync(redis, playerId) is { } previous ? await LobbyStore.GetAsync(redis, previous) : null;
        if (from is PartyLobby home && home.PlayerIds.Contains(playerId))
        {
            state["homes"] = new JsonObject { [playerId] = home.Id };
        }

        await redis.StringSetAsync($"lobby:{lobbyId}", Js.Stringify(state), TimeSpan.FromHours(1));
        await redis.StringSetAsync($"player_lobby:{playerId}", lobbyId, TimeSpan.FromHours(8));
        await redis.HashSetAsync($"connections:{playerId}", "lobby_id", lobbyId);
        log.LogInformation("Created arena lobby {Lobby} for {Player}", lobbyId, playerId);
        if (from is PartyLobby party)
        {
            foreach (string partner in party.PlayerIds.Where(p => p != playerId))
            {
                await LobbyInvites.SendAsync(redis, partner, lobbyId, LobbyInvites.Arena, playerId);
                log.LogInformation("Invited {Partner}, in party lobby {Party} with {Player}, into arena lobby {Lobby}", partner, party.Id, playerId, lobbyId);
            }
        }

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
        string character, string skin, string gameVersion) =>
        Lobby(lobbyId, playerId, [(new LobbyDocuments.Member(playerId, nowSeconds, preferences, character, skin), 0)], request, gameVersion);

    /// <summary>
    /// The lobby with <paramref name="members"/> on their teams (in lobby order); the request's values (the creator's, at
    /// create) for the leader where it sends them.
    /// </summary>
    internal static JsonObject Lobby(string lobbyId, string leaderId, IReadOnlyList<(LobbyDocuments.Member Member, int Team)> members,
        JsonObject? request, string gameVersion)
    {
        var teams = new JsonArray();
        for (int t = 0; t < TeamCount; t++)
        {
            var players = new JsonObject();
            for (int i = 0; i < members.Count; i++)
            {
                if (members[i].Team == t)
                {
                    players[members[i].Member.Id] = LobbyDocuments.TeamPlayer(members[i].Member.Id, members[i].Member.JoinedAt, i);
                }
            }

            teams.Add(new JsonObject { ["TeamIndex"] = t, ["Players"] = players, ["Length"] = players.Count });
        }

        JsonObject gameplay = [], autoParty = [], platforms = [], loadouts = [];
        foreach (var (m, _) in members)
        {
            bool leader = m.Id == leaderId;
            gameplay[m.Id] = m.GameplayPreferences.DeepClone();
            autoParty[m.Id] = (leader ? request?["AutoPartyPreference"]?.DeepClone() : null) ?? false;
            platforms[m.Id] = (leader ? request?["Platform"]?.DeepClone() : null) ?? "PC";
            loadouts[m.Id] = LobbyDocuments.Loadout(m.Character, m.Skin);
        }

        return new JsonObject
        {
            ["Teams"] = teams,
            ["LeaderID"] = leaderId,
            ["LobbyType"] = request?["LobbyType"]?.DeepClone() ?? 0,
            ["ReadyPlayers"] = new JsonObject(),
            ["PlayerGameplayPreferences"] = gameplay,
            ["PlayerAutoPartyPreferences"] = autoParty,
            ["GameVersion"] = gameVersion,
            ["HissCrc"] = request?["HissCrc"]?.DeepClone() ?? 0,
            ["Platforms"] = platforms,
            ["AllMultiplayParams"] = request?["AllMultiplayParams"]?.DeepClone() ?? LobbyDocuments.AllMultiplay(),
            ["LockedLoadouts"] = loadouts,
            // A guess: no answer from WB is known, and the lobby parser does not read it.
            ["ModeString"] = Mode,
            ["IsLobbyJoinable"] = true,
            ["MatchID"] = lobbyId,
            ["LobbyTemplate"] = Mode,
        };
    }

    public async Task<JsonObject> JoinAsync(JsonObject? claims, JsonObject? request, CancellationToken ct)
    {
        string? me = claims?["id"] is JsonValue v && v.TryGetValue(out string? id) && id.Length > 0 ? id : null;
        if (me is null || services.GetService<IConnectionMultiplexer>()?.GetDatabase() is not { } redis)
        {
            log.LogWarning("join_arena_lobby with no player id or no Redis; answered empty");
            return LobbyDocuments.Ssc([], returnCode: 1);
        }

        string? pending = await redis.StringGetAsync($"pending_join_lobby:{me}");
        string target = new[] { pending, Str(request, "LobbyId"), Str(request, "MatchID"), Str(request, "HostId") }.FirstOrDefault(t => !string.IsNullOrEmpty(t)) ?? "";
        if (await LobbyStore.GetAsync(redis, target) is not ArenaLobby lobby)
        {
            log.LogWarning("join_arena_lobby from {Player}: no arena lobby {Lobby} (pending {Pending})", me, target, pending);
            return LobbyDocuments.Ssc([], returnCode: 1);
        }

        // The party they come from, where a leave takes them back.
        var from = await LobbyStore.PointerAsync(redis, me) is { } previous ? await LobbyStore.GetAsync(redis, previous) : null;
        string? home = from is PartyLobby party && party.PlayerIds.Contains(me) ? party.Id : null;
        bool added = false;
        var joined = await LobbyStore.UpdateAsync(redis, target, changed =>
        {
            added = false;
            if (changed.PlayerIds.Contains(me))
            {
                return LobbyWrite.Keep;
            }

            if (changed is not ArenaLobby { IsFull: false })
            {
                return LobbyWrite.Keep;
            }

            changed.Join(me);
            if (home is not null)
            {
                ((ArenaLobby)changed).SetHome(me, home);
            }

            added = true;
            return LobbyWrite.Save;
        }) as ArenaLobby ?? lobby;
        if (!joined.PlayerIds.Contains(me))
        {
            log.LogWarning("join_arena_lobby from {Player}: arena lobby {Lobby} is full", me, target);
            return LobbyDocuments.Ssc([], returnCode: 1);
        }

        await LobbyStore.SetPointerAsync(redis, me, target, LobbyStore.PartyTtl);
        await redis.HashSetAsync($"connections:{me}", "lobby_id", target);
        if (!string.IsNullOrEmpty(pending))
        {
            await redis.KeyDeleteAsync($"pending_join_lobby:{me}");
        }

        var document = await DocumentAsync(redis, joined, lobbies.CurrentValue.GameVersion, time.GetUtcNow().ToUnixTimeSeconds());
        log.LogInformation("join_arena_lobby: {Player} {Did} arena lobby {Lobby} on team {Team} ({Count} players)", me, added ? "joined" : "is already in", target, joined.TeamOf(me), joined.PlayerIds.Count);
        if (added)
        {
            var others = joined.PlayerIds.Where(p => p != me).ToList();
            var notice = PlayerMessages.Update(new JsonObject
            {
                ["Player"] = new JsonObject
                {
                    ["Account"] = new JsonObject { ["id"] = me },
                    ["LobbyPlayerIndex"] = joined.PlayerIds.IndexOf(me),
                    ["JoinedAt"] = LobbyDocuments.Date(time.GetUtcNow().ToUnixTimeSeconds()),
                    ["BotSettingSlug"] = "",
                    ["CrossplayPreference"] = 1,
                },
                ["TeamIndex"] = joined.TeamOf(me),
                ["MatchID"] = target,
                ["Cluster"] = LobbyDocuments.Cluster,
                ["template_id"] = "PlayerJoinedLobby",
                ["LockedLoadouts"] = document["LockedLoadouts"]!.DeepClone(),
                ["ModeString"] = Mode,
            }, new JsonObject { ["match"] = new JsonObject { ["id"] = target } });
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(JoinNoticeDelay, time);
                    await PlayerMessages.SendAsync(redis, others, notice);
                }
                catch (Exception e)
                {
                    log.LogError("Telling arena lobby {Lobby} of {Player}'s join failed: {Error}", target, me, e.Message);
                }
            });
        }

        return LobbyDocuments.Answer(document);
    }

    /// <summary>
    /// The lobby's document from the store: each player on their team, their preferences (the stored value) and fighter
    /// (player:{player}, then the session's, then Shaggy); the leader joined when the lobby was made, the others at
    /// <paramref name="nowSeconds"/> (no join time is stored).
    /// </summary>
    internal static async Task<JsonObject> DocumentAsync(IDatabase redis, ArenaLobby lobby, string gameVersion, long nowSeconds)
    {
        var members = new List<(LobbyDocuments.Member, int)>();
        foreach (string pid in lobby.PlayerIds)
        {
            var connection = (await redis.HashGetAllAsync($"connections:{pid}")).ToDictionary(e => e.Name.ToString(), e => e.Value.ToString());
            var player = (await redis.HashGetAllAsync($"player:{pid}")).ToDictionary(e => e.Name.ToString(), e => e.Value.ToString());
            members.Add((new LobbyDocuments.Member(pid, pid == lobby.OwnerId ? lobby.CreatedSeconds : nowSeconds,
                JsonValue.Create(GameplayPreferences.Of(Get(connection, "GameplayPreferences"))),
                Or(Get(player, "character"), Get(connection, "character"), "character_shaggy"),
                Or(Get(player, "skin"), Get(connection, "skin"), "skin_shaggy_default")), lobby.TeamOf(pid)));
        }

        return Lobby(lobby.Id, lobby.OwnerId, members, new JsonObject { ["HissCrc"] = LobbyDocuments.HissCrc }, gameVersion);
    }

    // switch_custom_game_lobby_team, {MatchID, TeamIndex} (bench, 2026-10-10): the team, when it has room; everyone in the
    // lobby is told (PlayerSwitchedCustomLobbyTeams, the custom lobby's), and the answer echoes the custom lobby's.
    public async Task<JsonObject?> SwitchTeamAsync(string playerId, JsonObject? request, CancellationToken ct)
    {
        if (await ArenaOfAsync(request, "MatchID") is not { } found)
        {
            return null;
        }

        var (redis, lobby) = found;
        // A number of any kind (a body parsed from JSON holds a JsonElement; one built in code, its own type).
        int team = request?["TeamIndex"] is JsonValue t && t.GetValueKind() == System.Text.Json.JsonValueKind.Number
            && int.TryParse(t.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture, out int n) ? n : -1;
        bool moved = false;
        var changed = await LobbyStore.UpdateAsync(redis, lobby.Id, l =>
        {
            moved = ((ArenaLobby)l).SwitchTeam(playerId, team);
            return moved ? LobbyWrite.Save : LobbyWrite.Keep;
        }) as ArenaLobby ?? lobby;
        if (!moved)
        {
            log.LogInformation("switch_custom_game_lobby_team in arena lobby {Lobby}: {Player} not moved to team {Team}", lobby.Id, playerId, team);
            return [];
        }

        var player = LobbyDocuments.TeamPlayer(playerId, time.GetUtcNow().ToUnixTimeSeconds(), changed.PlayerIds.IndexOf(playerId));
        log.LogInformation("switch_custom_game_lobby_team: {Player} moved to team {Team} in arena lobby {Lobby}", playerId, team, lobby.Id);
        await PlayerMessages.SendAsync(redis, changed.PlayerIds, Notice(new JsonObject
        {
            ["template_id"] = "PlayerSwitchedCustomLobbyTeams",
            ["MatchID"] = lobby.Id,
            ["Player"] = player.DeepClone(),
            ["TeamIndex"] = team,
        }, lobby.Id));
        return LobbyDocuments.Ssc(new JsonObject { ["MatchID"] = lobby.Id, ["Player"] = player, ["TeamIndex"] = team });
    }

    // kick_from_lobby, {KickeeAccountID, LobbyId} (bench, 2026-10-10): the leader only. The lobby rule for every kind: the
    // kick disbands the kicked player's party, so they go back to a solo party lobby of their own (their home party lobby
    // loses them; their next create_party_lobby makes the new one), and a partner who stays keeps the home party lobby, alone.
    // Everyone, the kicked player included, is told (PlayerKickedFromLobby, the custom lobby's).
    public async Task<JsonObject?> KickAsync(string playerId, JsonObject? request, CancellationToken ct)
    {
        if (await ArenaOfAsync(request, "LobbyId", "MatchID") is not { } found)
        {
            return null;
        }

        var (redis, lobby) = found;
        string kickee = Str(request, "KickeeAccountID") ?? "";
        if (lobby.OwnerId != playerId || kickee == playerId || !lobby.PlayerIds.Contains(kickee))
        {
            log.LogWarning("kick_from_lobby in arena lobby {Lobby}: {Player} may not kick {Kickee} (leader {Leader})", lobby.Id, playerId, kickee, lobby.OwnerId);
            return LobbyDocuments.Ssc(new JsonObject { ["LobbyId"] = lobby.Id, ["Player"] = null }, 1);
        }

        var everyone = lobby.PlayerIds.ToList();
        string? home = lobby.HomeOf(kickee);
        var player = LobbyDocuments.TeamPlayer(kickee, time.GetUtcNow().ToUnixTimeSeconds(), lobby.PlayerIds.IndexOf(kickee));
        await LobbyStore.UpdateAsync(redis, lobby.Id, l =>
        {
            ((ArenaLobby)l).Remove(kickee);
            return LobbyWrite.Save;
        });
        await DisbandAsync(redis, kickee, home);
        log.LogInformation("kick_from_lobby: {Player} kicked {Kickee} from arena lobby {Lobby}; their party (lobby {Home}) is disbanded", playerId, kickee, lobby.Id, home);
        await PlayerMessages.SendAsync(redis, everyone, Notice(new JsonObject
        {
            ["MatchID"] = lobby.Id,
            ["template_id"] = "PlayerKickedFromLobby",
            ["Player"] = player.DeepClone(),
            ["KickeeAccountID"] = kickee,
        }, lobby.Id));
        return LobbyDocuments.Ssc(new JsonObject { ["LobbyId"] = lobby.Id, ["Player"] = player });
    }

    // leave_player_lobby from an Arena lobby (PartyService hands it here). The leaver goes back to the party lobby they came
    // from, and the partner they came with is invited back to it (a Party invite: their game asks, as it did to come in).
    // The others are told (PlayerLeftLobby); a leaving leader passes the lead to the longest-tenured player (Remove).
    public async Task<string?> LeaveAsync(string playerId, string lobbyId, bool bringPartners = true)
    {
        if (services.GetService<IConnectionMultiplexer>()?.GetDatabase() is not { } redis
            || await LobbyStore.GetAsync(redis, lobbyId) is not ArenaLobby lobby || !lobby.PlayerIds.Contains(playerId))
        {
            return null;
        }

        string? home = lobby.HomeOf(playerId);
        var partners = home is null || !bringPartners ? [] : lobby.PlayerIds.Where(p => p != playerId && lobby.HomeOf(p) == home).ToList();
        var player = LobbyDocuments.TeamPlayer(playerId, time.GetUtcNow().ToUnixTimeSeconds(), lobby.PlayerIds.IndexOf(playerId));
        var left = await LobbyStore.UpdateAsync(redis, lobbyId, l =>
        {
            ((ArenaLobby)l).Remove(playerId);
            return l.PlayerIds.Count == 0 ? LobbyWrite.Delete : LobbyWrite.Save;
        });
        if (left is { PlayerIds.Count: > 0 })
        {
            await PlayerMessages.SendAsync(redis, left.PlayerIds, Notice(new JsonObject
            {
                ["MatchID"] = lobbyId,
                ["template_id"] = "PlayerLeftLobby",
                ["Player"] = player,
                ["ReadyPlayers"] = new JsonObject(),
                ["NewLeader"] = left.OwnerId,
            }, lobbyId));
        }

        if (home is null || await LobbyStore.GetAsync(redis, home) is not PartyLobby party || !party.PlayerIds.Contains(playerId))
        {
            log.LogInformation("{Player} left arena lobby {Lobby}: no party lobby to go back to", playerId, lobbyId);
            return null;
        }

        await LobbyStore.SetPointerAsync(redis, playerId, home, LobbyStore.PartyTtl);
        await redis.HashSetAsync($"connections:{playerId}", "lobby_id", home);
        foreach (string partner in partners)
        {
            await LobbyInvites.SendAsync(redis, partner, home, LobbyInvites.Party, playerId);
        }

        log.LogInformation("{Player} left arena lobby {Lobby} for party lobby {Home}; invited back: [{Partners}]", playerId, lobbyId, home, string.Join(", ", partners));
        return home;
    }

    // The kicked player's party ends: they leave their home party lobby (deleted when it is left empty) and their pointer.
    private static async Task DisbandAsync(IDatabase redis, string kickee, string? home)
    {
        if (home is not null)
        {
            await LobbyStore.UpdateAsync(redis, home, party =>
            {
                party.PlayerIds.Remove(kickee);
                return party.PlayerIds.Count == 0 ? LobbyWrite.Delete : LobbyWrite.Save;
            });
        }

        await LobbyStore.ClearPointerAsync(redis, kickee);
        await redis.KeyDeleteAsync($"pending_join_lobby:{kickee}");
    }

    private async Task<(IDatabase Redis, ArenaLobby Lobby)?> ArenaOfAsync(JsonObject? request, params string[] keys)
    {
        string id = keys.Select(k => Str(request, k)).FirstOrDefault(v => v is not null) ?? "";
        return id.Length > 0 && services.GetService<IConnectionMultiplexer>()?.GetDatabase() is { } redis
            && await LobbyStore.GetAsync(redis, id) is ArenaLobby lobby
            ? (redis, lobby)
            : null;
    }

    // The custom lobby's notice envelope, which the Arena lobby screen shares.
    private static JsonObject Notice(JsonObject data, string lobbyId) => new()
    {
        ["data"] = data,
        ["payload"] = new JsonObject { ["custom_notification"] = "realtime", ["match"] = new JsonObject { ["id"] = lobbyId } },
        ["cmd"] = "update",
        ["header"] = "",
    };

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

    private static string? Str(JsonObject? body, string key) => body?[key] is JsonValue v && v.TryGetValue(out string? s) && s.Length > 0 ? s : null;

    private static string? Get(Dictionary<string, string> hash, string field) => hash.TryGetValue(field, out var value) ? value : null;

    private static string Or(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? "";
}

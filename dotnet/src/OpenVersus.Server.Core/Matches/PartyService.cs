using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Clients;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Cosmetics;
using OpenVersus.Server.Core.FunFacts;
using OpenVersus.Server.Core.Preferences;
using OpenVersus.Server.Core.Realtime;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Matches;

// The party lobby SSC routes, ported from the TS server (ssc/ssc.ts, modules/lobby/shared.routes.ts, ssc/routes.ts,
// services/lobbyService.ts) together with what the TS websocket sent the players for them (websocket.ts
// handleOnLobbyModeChanged, handlePartyInvite, handlePlayerLoadoutLocked, the custom_lobby:notification relay): those
// messages now go through ws:send (PlayerMessages). The custom lobby side of the shared routes is CustomLobbyService's
// (CustomLobbies/); CustomLobbyAsync says which requests are.
//
// Redis, the TS server's keys (MIGRATION-BRIDGES.md 2):
//   lobby:{id}                  JSON {lobbyId, ownerId, ownerUsername, mode, playerIds, createdAt (ms)} (+ joinable false
//                               once not joinable); EX 8 h with 2+ players, else 1 h (the shared leave writes 1 h)
//   player_lobby:{player}       lobby id, EX 8 h (the shared leave writes 1 h)
//   pending_join_lobby:{player} lobby id, EX 60 s: the lobby an invited (or left) player's next join goes to
//   player:{player}:lobby:{id}  hash {id, created_at (ISO), mode, owner}, no TTL: the TS createLobby's record, which
//                               set_mode_for_lobby checks for ownership
//   connections:{player}        lobby_id written; the session read for names, GameplayPreferences, character, skin
//   player:{player}             character, skin, ip (+ profileIcon at create_party_lobby): the loadout the matchmaker reads
//   party_ready:{lobby}         set of ready players, EX 1 h
//   connections:{player}:cosmetics  the match's copy of every lobby player's cosmetics (ICosmeticsService.WriteMatchCopyAsync)
//   online_players              read: a party is only rejoined when everyone in it is online
//   dll_notifications:{player}  party_left notices for the OpenVersus client
// Published: matchmaking:cancel {playersIds, matchmakingId: "party-changed"} when someone joins a party (the TS websocket
// keeps the queue ticket and its tick: it cancels them; with Realtime:Gateway on, cancelled here: MatchmakingQueue).
//
// Client gate: readying up in a party lobby is what the game does before it sends its matchmaking request, and the one
// refusal on that path it backs out of (a refused matchmaking request leaves it waiting for its ticket, cancel disabled).
// So a ready from a party with anyone who must update is refused (BlockOutdatedAsync), each of them toasted; un-readying
// is never refused. A custom lobby's or a rift lobby's ready is not gated: starting its match is (in a rift lobby the game
// does not back out of a refused ready; it waits on its loading screen).
//
// Differences from the TS server, none on the wire:
//   - lobby_id is written to the session as one field; the TS server read the whole session and wrote it all back,
//     losing a change made in between (a GameplayPreferences update, say).
//   - the IP-keyed session copy (connections:{ip}) gets lobby_id only when it is this player's; the TS check compared
//     the copy's id with itself, so it always wrote, into another household member's copy too.
//   - set_mode_for_lobby no longer reads every session in Redis (KEYS connections:*) for a debug log.
//   - party keys (the retired /party page) are not updated: nothing makes them any more.
//   - a mode change is sent to everyone in the party; the TS server told only the player who changed it.
//   - an invite that names no lobby is not sent (it could not be accepted); the TS server sent it with an empty MatchID.
//   - a loadout lock with a disabled character, or from a player with no record, is answered (bAreAllLoadoutsLocked
//     false, as the rift lock answers); the TS server never answered it.
//   - a player with no session (connections:{player}) is still the player their token names: their cosmetics go into
//     the match copy, and their lock is recorded and answered. The TS server looked the session's id up, found none,
//     wrote the cosmetics of "undefined" (player:undefined:cosmetics, shared by every such player) and, for a lock,
//     never answered.

/// <summary>Who is asking: the session's account id and token claims, their address, and the request body.</summary>
public sealed record PartyRequest(string AccountId, JsonObject? Claims, string ClientIp, JsonObject? Body);

public interface IPartyService
{
    /// <summary>The custom lobby a shared route's request belongs to (ICustomLobbyService answers those), or null.</summary>
    Task<string?> CustomLobbyAsync(string route, PartyRequest request);

    Task<JsonObject> CreatePartyLobbyAsync(PartyRequest request, CancellationToken ct = default);
    Task<JsonObject> CreatePartyAsync(PartyRequest request, CancellationToken ct = default);
    Task<JsonObject> SetModeAsync(PartyRequest request, CancellationToken ct = default);
    Task<JsonObject> InviteAsync(PartyRequest request, CancellationToken ct = default);
    Task<JsonObject> JoinAsync(PartyRequest request, CancellationToken ct = default);
    Task<JsonObject> LeaveAsync(PartyRequest request, CancellationToken ct = default);
    Task<JsonObject> SetNotJoinableAsync(PartyRequest request, CancellationToken ct = default);
    Task<JsonObject> SetReadyAsync(PartyRequest request, CancellationToken ct = default);
    Task<JsonObject> LockLoadoutAsync(PartyRequest request, CancellationToken ct = default);
}

internal sealed class PartyService(IServiceProvider services, ICosmeticsService cosmetics, IFunFacts funFacts, IClientUpdateGate gate, IOptionsMonitor<LobbySettings> settings,
    TimeProvider time, ILogger<PartyService> log) : IPartyService
{
    public const string CancelMatchmakingChannel = "matchmaking:cancel";

    // The TS server's delays before telling the other players, after its answer: the game handles the answer first.
    internal static TimeSpan JoinNoticeDelay = TimeSpan.FromMilliseconds(500);
    internal static TimeSpan LockNoticeDelay = TimeSpan.FromMilliseconds(200);

    private static readonly TimeSpan PartyTtl = TimeSpan.FromHours(8);
    private static readonly TimeSpan SoloTtl = TimeSpan.FromHours(1);

    // The characters a lobby may not be made or locked with (ssc.ts); the lock's list adds the capitalised C022.
    private static readonly HashSet<string> s_disabledAtCreate = ["character_Meeseeks", "Meeseeks", "character_supershaggy", "supershaggy", "character_c022", "c022"];
    private static readonly HashSet<string> s_disabledAtLock = [.. s_disabledAtCreate, "character_C022", "C022"];

    private IDatabase Redis() => services.GetService<IConnectionMultiplexer>()?.GetDatabase() ?? throw new InvalidOperationException("this service has no Redis (REDIS)");

    public async Task<string?> CustomLobbyAsync(string route, PartyRequest request)
    {
        var redis = Redis();
        var body = request.Body;
        string lobbyId = route switch
        {
            "invite_to_player_lobby" => FirstStr(body, "LobbyId", "MatchID"),
            "set_ready_for_lobby" => FirstStr(body, "MatchID", "LobbyId"),
            _ => FirstStr(body, "LobbyId"),
        };
        if (lobbyId.Length > 0 && await redis.KeyExistsAsync($"custom_lobby_ssc:{lobbyId}"))
        {
            return lobbyId;
        }

        // create_party_lobby and leave_player_lobby also follow the player into their custom lobby.
        if (route is "create_party_lobby" or "leave_player_lobby"
            && await redis.StringGetAsync($"ssc_custom_lobby_player:{request.AccountId}") is { IsNullOrEmpty: false } mine)
        {
            return route == "create_party_lobby" && !await redis.KeyExistsAsync($"custom_lobby_ssc:{mine}") ? null : mine.ToString();
        }

        return null;
    }

    // ── create_party_lobby ──────────────────────────────────────────────────────────────────────────────────────────
    public async Task<JsonObject> CreatePartyLobbyAsync(PartyRequest request, CancellationToken ct)
    {
        var redis = Redis();
        string me = request.AccountId;
        string ip = LoadoutIp(Str(request.Claims, "current_ip") is { Length: > 0 } claimed ? claimed : request.ClientIp);

        // A fun fact, once per login: /access arms the flag for 60 s and the first lobby after it takes it.
        if (await redis.StringGetDeleteAsync($"fun_fact_pending:{me}") is { HasValue: true })
        {
            if (await funFacts.RandomAsync(me, ct) is { } fact)
            {
                await PlayerMessages.NotifyClientAsync(redis, me, "admin_banner", fact.Title, fact.Message, new JsonObject { ["timeout"] = 10 }, NowMs());
                log.LogInformation("Pushed fun fact to {Player}: \"{Title} — {Message}\"", me, fact.Title, fact.Message);
            }
        }

        // The player's record as mongoose reads it: a field it does not have is the schema's default (PlayerTester.ts);
        // one that is there but null stays null, and the icon then falls back.
        string character = "", skin = "", profileIcon = "";
        if (await PlayerRecordAsync(me, ct) is { } record)
        {
            character = Field(record, "character", "character_shaggy") ?? "";
            skin = Field(record, "variant", "skin_shaggy_default") ?? "";
            profileIcon = Field(record, "profile_icon", "profile_icon_default") is { Length: > 0 } icon ? icon : "profile_icon_default_gold";
        }

        var (shownCharacter, shownSkin) = s_disabledAtCreate.Contains(character) ? ("character_shaggy", "skin_shaggy_default") : (character, skin);
        log.LogInformation("Received request to create party lobby for AccountId {Player} with character: {Character} and IP: {Ip}", me, shownCharacter, ip);

        var connection = await HashAsync(redis, $"connections:{me}");
        await cosmetics.WriteMatchCopyAsync(me, await cosmetics.EquippedAsync(me, ct));
        await redis.HashSetAsync($"player:{me}", [new("character", character), new("skin", skin), new("ip", ip), new("profileIcon", profileIcon)]);

        // REJOIN: the player is in a party lobby with others, all of them online.
        if (await redis.StringGetAsync($"player_lobby:{me}") is { IsNullOrEmpty: false } existingId
            && await LobbyAsync(redis, existingId!) is { } existing && existing.PlayerIds.Count > 1 && existing.PlayerIds.Contains(me))
        {
            var online = (await redis.SetMembersAsync("online_players")).Select(v => v.ToString()).ToHashSet();
            if (existing.PlayerIds.Where(p => p != me).All(online.Contains))
            {
                log.LogInformation("REJOIN: Player {Player} is already in multi-player lobby {Lobby}, returning existing lobby data", me, (string?)existingId);
                await SaveLobbyAsync(redis, existing);
                foreach (string pid in existing.PlayerIds)
                {
                    await redis.StringSetAsync($"player_lobby:{pid}", existing.Id, PartyTtl);
                }

                return LobbyDocuments.Answer(await LobbyOfAsync(redis, existing, ModeOf(existing), writeCosmetics: true, ct));
            }

            log.LogInformation("REJOIN SKIPPED: Lobby {Lobby} has offline players, cleaning up stale data", (string?)existingId);
            existing.PlayerIds.Remove(me);
            if (existing.PlayerIds.Count == 0)
            {
                await redis.KeyDeleteAsync($"lobby:{existing.Id}");
            }
            else
            {
                await SaveLobbyAsync(redis, existing);
            }

            await redis.KeyDeleteAsync($"player_lobby:{me}");
        }

        var lobby = await NewLobbyAsync(redis, me, connection);
        var member = new LobbyDocuments.Member(me, Now(), Preferences(connection), shownCharacter, shownSkin);
        return LobbyDocuments.Answer(LobbyDocuments.Lobby([member], me, settings.CurrentValue.GameVersion, "1v1", lobby.Id));
    }

    // ── create_party (PartyManager::CreateParty: a flat {MatchID}) ───────────────────────────────────────────────────
    public async Task<JsonObject> CreatePartyAsync(PartyRequest request, CancellationToken ct)
    {
        var redis = Redis();
        string me = request.AccountId;
        string? lobbyId = await redis.StringGetAsync($"player_lobby:{me}");
        if (string.IsNullOrEmpty(lobbyId))
        {
            lobbyId = (await NewLobbyAsync(redis, me, await HashAsync(redis, $"connections:{me}"))).Id;
            log.LogInformation("create_party: Created new lobby {Lobby} for player {Player}", lobbyId, me);
        }

        return LobbyDocuments.Ssc(new JsonObject { ["MatchID"] = lobbyId });
    }

    // ── set_mode_for_lobby ──────────────────────────────────────────────────────────────────────────────────────────
    public async Task<JsonObject> SetModeAsync(PartyRequest request, CancellationToken ct)
    {
        var redis = Redis();
        string me = request.AccountId;
        if (await redis.StringGetAsync($"player_lobby:{me}") is not { IsNullOrEmpty: false } lobbyIdValue)
        {
            log.LogWarning("set_lobby_mode: No lobby found in Redis for player {Player}", me);
            return [];
        }

        string lobbyId = lobbyIdValue!;
        var mode = request.Body?["ModeString"]?.DeepClone();
        string modeText = mode is JsonValue mv && mv.TryGetValue(out string? s) ? s : Js.Stringify(mode);

        // changeLobbyMode: only the player who made the lobby (its player:{owner}:lobby:{id} record) changes it.
        var record = await HashAsync(redis, $"player:{me}:lobby:{lobbyId}");
        if (Get(record, "id") is null)
        {
            log.LogError("Lobby not found for id: {Lobby}", lobbyId);
        }
        else if (Get(record, "owner") != me)
        {
            log.LogError("You are not the owner of this lobby");
        }
        else
        {
            await redis.HashSetAsync($"player:{me}:lobby:{lobbyId}", "mode", modeText);
            // Everyone in the party hears of it (the TS server told only the player who changed it).
            var party = await LobbyAsync(redis, lobbyId) is { } current && current.PlayerIds.Contains(me) ? current.PlayerIds : [me];
            await PlayerMessages.SendAsync(redis, party, PlayerMessages.Update(new JsonObject
            {
                ["template_id"] = "OnLobbyModeUpdated",
                ["LobbyId"] = lobbyId,
                ["ModeString"] = modeText,
            }));
            var connection = await HashAsync(redis, $"connections:{me}");
            if (lobbyId != Get(connection, "lobby_id"))
            {
                await redis.HashSetAsync($"connections:{me}", "lobby_id", lobbyId);
                await MirrorLobbyIdAsync(redis, Get(connection, "current_ip"), me, lobbyId);
            }
        }

        if (await LobbyAsync(redis, lobbyId) is { PlayerIds.Count: > 1 } lobby)
        {
            log.LogInformation("set_lobby_mode: Lobby {Lobby} has {Count} players, returning full lobby data", lobbyId, lobby.PlayerIds.Count);
            return LobbyDocuments.Answer(await LobbyOfAsync(redis, lobby, mode, writeCosmetics: false, ct));
        }

        return LobbyDocuments.Ssc([]);
    }

    // ── invite_to_player_lobby (a party lobby; custom lobbies are CustomLobbyService's) ───────────────────────────────────
    public async Task<JsonObject> InviteAsync(PartyRequest request, CancellationToken ct)
    {
        var redis = Redis();
        string me = request.AccountId;
        var body = request.Body;
        // The client may send the invitee under various names.
        string invitee = FirstStr(body, "InviteeAccountID", "InviteeAccountId", "invitee_account_id", "TargetAccountId", "target_account_id",
            "AccountId", "account_id", "FriendAccountId", "friend_account_id");
        string lobbyId = FirstStr(body, "LobbyId", "lobby_id", "MatchID", "match_id");
        log.LogInformation("Party invite from {Inviter} to {Invitee} for lobby {Lobby}", me, invitee, lobbyId);

        if (invitee.Length == 0)
        {
            log.LogWarning("Could not determine invited player ID from request body");
            return LobbyDocuments.Ssc([]);
        }

        // An invite to no lobby could not be accepted (the join has nowhere to go): not sent.
        if (lobbyId.Length == 0)
        {
            log.LogWarning("Invite from {Inviter} to {Invitee} names no lobby; not sent", me, invitee);
            return LobbyDocuments.Ssc([]);
        }

        // The game does not always hide "+" once the party is full: refused here.
        if (await LobbyAsync(redis, lobbyId) is { } lobby)
        {
            if (lobby.PlayerIds.Count >= 2)
            {
                log.LogWarning("Invite blocked: lobby {Lobby} already has {Count} players (full)", lobbyId, lobby.PlayerIds.Count);
                return LobbyDocuments.Ssc([]);
            }

            if (lobby.PlayerIds.Contains(invitee))
            {
                log.LogWarning("Invite blocked: {Invitee} is already in lobby {Lobby}", invitee, lobbyId);
                return LobbyDocuments.Ssc([]);
            }
        }

        await ProfileNotifications.SendAsync(redis, invitee, new JsonObject
        {
            ["template_id"] = "InviteReceivedForLobby",
            ["LobbyType"] = 0,
            ["MatchID"] = lobbyId,
            ["ContextData"] = new JsonObject { ["LobbyType"] = "Party" },
            ["IsSpectator"] = false,
            ["InviterAccountId"] = me,
        });

        // The invitee's join goes to this lobby when they accept.
        await redis.StringSetAsync($"pending_join_lobby:{invitee}", lobbyId, TimeSpan.FromSeconds(60));

        return LobbyDocuments.Ssc([]);
    }

    // ── join_party_lobby ────────────────────────────────────────────────────────────────────────────────────────────
    public async Task<JsonObject> JoinAsync(PartyRequest request, CancellationToken ct)
    {
        var redis = Redis();
        string me = request.AccountId;
        var body = request.Body;
        string? pending = await redis.StringGetAsync($"pending_join_lobby:{me}");
        string? mapped = await redis.StringGetAsync($"player_lobby:{me}");
        string? fromBody = new[] { "LobbyId", "lobbyId", "MatchID", "matchId" }.Select(k => Str(body, k)).FirstOrDefault(v => !string.IsNullOrEmpty(v));
        // An accepted invite (pending) wins over the player's own lobby, which wins over what the game sends.
        string? target = new[] { pending, mapped, fromBody }.FirstOrDefault(v => !string.IsNullOrEmpty(v));
        log.LogInformation("join_party_lobby: Player {Player}: pending={Pending}, body={Body}, redis={Mapped}", me, pending, fromBody, mapped);
        if (target is null)
        {
            log.LogWarning("join_party_lobby: No lobby ID found for player {Player}", me);
            return LobbyDocuments.Ssc([], returnCode: 200);
        }

        if (await LobbyAsync(redis, target) is not { } lobby)
        {
            log.LogWarning("join_party_lobby: Lobby {Lobby} not found: invite expired", target);
            return LobbyDocuments.Ssc([], returnCode: 1);
        }

        // The owner went into a custom lobby: the lobby the game waits for, with only the joining player in it.
        if (await redis.KeyExistsAsync($"ssc_custom_lobby_player:{lobby.OwnerId}"))
        {
            log.LogWarning("join_party_lobby: Owner {Owner} is in custom lobby: returning stale lobby with just invitee", lobby.OwnerId);
            var connection = await HashAsync(redis, $"connections:{me}");
            var loadout = await HashAsync(redis, $"player:{me}");
            lobby.PlayerIds.Clear();
            lobby.PlayerIds.Add(me);
            lobby.OwnerId = me;
            await SaveLobbyAsync(redis, lobby);
            await redis.StringSetAsync($"player_lobby:{me}", target, PartyTtl);
            if (!string.IsNullOrEmpty(pending))
            {
                await redis.KeyDeleteAsync($"pending_join_lobby:{me}");
            }

            var alone = new LobbyDocuments.Member(me, Now(), Preferences(connection), Or(Get(loadout, "character"), "character_shaggy"), Or(Get(loadout, "skin"), "skin_shaggy_default"));
            return LobbyDocuments.Answer(LobbyDocuments.Lobby([alone], me, settings.CurrentValue.GameVersion, "1v1", target));
        }

        if (!lobby.PlayerIds.Contains(me))
        {
            log.LogInformation("join_party_lobby: Adding player {Player} to lobby {Lobby}", me, target);
            lobby.PlayerIds.Add(me);
            await SaveLobbyAsync(redis, lobby);
        }

        await redis.StringSetAsync($"player_lobby:{me}", target, PartyTtl);
        if (!string.IsNullOrEmpty(pending))
        {
            await redis.KeyDeleteAsync($"pending_join_lobby:{me}");
        }

        var answerLobby = await LobbyOfAsync(redis, lobby, ModeOf(lobby), writeCosmetics: true, ct);
        // Only a player joining someone else's party is announced (the owner's own join would loop).
        if (me != lobby.OwnerId)
        {
            if (MatchLaunches.Gateway(services))
            {
                await MatchmakingQueue.CancelAsync(redis, lobby.PlayerIds, "party-changed");
            }
            else
            {
                await redis.PublishAsync(RedisChannel.Literal(CancelMatchmakingChannel), Js.Stringify(new JsonObject
                {
                    ["playersIds"] = new JsonArray(lobby.PlayerIds.Select(p => (JsonNode?)p).ToArray()),
                    ["matchmakingId"] = "party-changed",
                }));
            }
            var others = lobby.PlayerIds.Where(p => p != me).ToList();
            var notice = PlayerMessages.Update(new JsonObject
            {
                ["Player"] = new JsonObject
                {
                    ["Account"] = new JsonObject { ["id"] = me },
                    ["LobbyPlayerIndex"] = lobby.PlayerIds.IndexOf(me),
                    ["JoinedAt"] = LobbyDocuments.Date(Now()),
                    ["BotSettingSlug"] = "",
                    ["CrossplayPreference"] = 1,
                },
                ["TeamIndex"] = 0,
                ["MatchID"] = target,
                ["Cluster"] = LobbyDocuments.Cluster,
                ["template_id"] = "PlayerJoinedLobby",
                ["LockedLoadouts"] = answerLobby["LockedLoadouts"]!.DeepClone(),
                ["ModeString"] = ModeOf(lobby),
            }, new JsonObject { ["match"] = new JsonObject { ["id"] = target } });
            After(JoinNoticeDelay, async () =>
            {
                await PlayerMessages.SendAsync(redis, others, notice);
                foreach (string other in others)
                {
                    await redis.StringSetAsync($"pending_join_lobby:{other}", target, TimeSpan.FromSeconds(60));
                    await redis.StringSetAsync($"player_lobby:{other}", target, PartyTtl);
                }
            });
        }

        return LobbyDocuments.Answer(answerLobby);
    }

    // ── leave_player_lobby (a party lobby; custom lobbies are CustomLobbyService's) ───────────────────────────────────────
    public async Task<JsonObject> LeaveAsync(PartyRequest request, CancellationToken ct)
    {
        var redis = Redis();
        string me = request.AccountId;

        // The lobby the game names, when the player is in it: they leave it for a new solo lobby.
        if (Str(request.Body, "LobbyId") is { } named && await LobbyAsync(redis, named) is { } left && left.PlayerIds.Contains(me))
        {
            log.LogInformation("leave_player_lobby: Player {Player} leaving party lobby {Lobby}", me, named);
            left.PlayerIds.Remove(me);
            if (left.PlayerIds.Count == 0)
            {
                await redis.KeyDeleteAsync($"lobby:{named}");
            }
            else
            {
                await redis.StringSetAsync($"lobby:{named}", left.ToJson(), SoloTtl);
            }

            await redis.KeyDeleteAsync($"player_lobby:{me}");
            await redis.KeyDeleteAsync($"party_ready:{named}");
            if (left.PlayerIds.Count > 0)
            {
                // This one message has its payload and its last two keys the other way round (shared.routes.ts).
                await PlayerMessages.SendAsync(redis, left.PlayerIds, new JsonObject
                {
                    ["data"] = new JsonObject
                    {
                        ["MatchID"] = named,
                        ["template_id"] = "PlayerLeftLobby",
                        ["Player"] = new JsonObject
                        {
                            ["Account"] = new JsonObject { ["id"] = me },
                            ["LobbyPlayerIndex"] = 0,
                            ["JoinedAt"] = LobbyDocuments.Date(Now()),
                            ["BotSettingSlug"] = "",
                            ["CrossplayPreference"] = 1,
                        },
                        ["ReadyPlayers"] = new JsonObject(),
                        ["NewLeader"] = left.PlayerIds[0],
                    },
                    ["payload"] = new JsonObject { ["custom_notification"] = "realtime", ["match"] = new JsonObject { ["id"] = named } },
                    ["cmd"] = "update",
                    ["header"] = "",
                });
            }

            string soloId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));
            var connection = await HashAsync(redis, $"connections:{me}");
            await redis.StringSetAsync($"lobby:{soloId}", new LobbyState(soloId, me, Or(Get(connection, "username"), Get(connection, "hydraUsername"), "Unknown"), "1v1", [me], NowMs(), null).ToJson(), SoloTtl);
            await redis.StringSetAsync($"player_lobby:{me}", soloId, SoloTtl);
            var member = new LobbyDocuments.Member(me, Now(), Preferences(connection), Or(Get(connection, "character"), "character_shaggy"), Or(Get(connection, "skin"), "skin_shaggy_default"));
            return LobbyDocuments.Answer(LobbyDocuments.Lobby([member], me, "local", "1v1", soloId));
        }

        // Otherwise the player's own lobby: a step of joining another (pending join), or a real leave.
        if (await redis.StringGetAsync($"player_lobby:{me}") is not { IsNullOrEmpty: false } lobbyIdValue)
        {
            log.LogInformation("leave_player_lobby: Player {Player} not in any lobby, returning OK", me);
            return LobbyDocuments.Ssc([]);
        }

        string lobbyId = lobbyIdValue!;
        if (await LobbyAsync(redis, lobbyId) is not { } lobby)
        {
            log.LogWarning("leave_player_lobby: Lobby {Lobby} not found, cleaning stale mapping", lobbyId);
            await redis.KeyDeleteAsync($"player_lobby:{me}");
            return LobbyDocuments.Ssc([]);
        }

        if (await redis.StringGetAsync($"pending_join_lobby:{me}") is { IsNullOrEmpty: false } pendingJoin)
        {
            log.LogInformation("leave_player_lobby: JOIN TRANSITION for {Player} (pending_join={Pending})", me, (string?)pendingJoin);
            lobby.PlayerIds.Remove(me);
            await redis.KeyDeleteAsync($"player_lobby:{me}");
            if (lobby.PlayerIds.Count > 0 || (string?)pendingJoin == lobbyId)
            {
                // An empty lobby the player is about to rejoin is kept for the join.
                await SaveLobbyAsync(redis, lobby);
            }
            else
            {
                await redis.KeyDeleteAsync($"lobby:{lobbyId}");
            }

            return LobbyDocuments.Ssc([]);
        }

        await GenuineLeaveAsync(redis, me, lobby);
        return LobbyDocuments.Ssc([]);
    }

    // performGenuineLeave: the owner keeps the lobby, everyone else gets a new solo lobby; the OpenVersus client is told
    // (party_left) and rejoins.
    private async Task GenuineLeaveAsync(IDatabase redis, string leaving, LobbyState lobby)
    {
        if (lobby.PlayerIds.Count <= 1)
        {
            await redis.KeyDeleteAsync($"player_lobby:{leaving}");
            await redis.KeyDeleteAsync($"lobby:{lobby.Id}");
            log.LogInformation("genuineLeave: Solo lobby {Lobby} deleted", lobby.Id);
            return;
        }

        string owner = lobby.OwnerId;
        var nonOwners = lobby.PlayerIds.Where(p => p != owner).ToList();
        lobby.PlayerIds.Clear();
        lobby.PlayerIds.Add(owner);
        lobby.Mode = "1v1";
        await SaveLobbyAsync(redis, lobby);
        await redis.StringSetAsync($"pending_join_lobby:{owner}", lobby.Id, TimeSpan.FromSeconds(60));
        await PlayerMessages.NotifyClientAsync(redis, owner, "party_left", "Party Update", owner == leaving ? "Returning to solo lobby" : "Your party member left",
            new JsonObject { ["newLobbyId"] = lobby.Id }, NowMs());

        foreach (string pid in nonOwners)
        {
            var solo = await NewLobbyAsync(redis, pid, await HashAsync(redis, $"connections:{pid}"));
            await redis.StringSetAsync($"pending_join_lobby:{pid}", solo.Id, TimeSpan.FromSeconds(60));
            await PlayerMessages.NotifyClientAsync(redis, pid, "party_left", "Party Update", pid == leaving ? "Returning to solo lobby" : "Your party member left",
                new JsonObject { ["newLobbyId"] = solo.Id }, NowMs());
        }
    }

    // ── set_lobby_not_joinable (matchmaking starts: a stale join must fail) ──────────────────────────────────────────
    public async Task<JsonObject> SetNotJoinableAsync(PartyRequest request, CancellationToken ct)
    {
        var redis = Redis();
        if (Str(request.Body, "LobbyId") is { Length: > 0 } lobbyId && await redis.StringGetAsync($"lobby:{lobbyId}") is { HasValue: true } raw)
        {
            try
            {
                if (Js.Parse(raw.ToString()) is JsonObject state)
                {
                    state["joinable"] = false;
                    await redis.StringSetAsync($"lobby:{lobbyId}", Js.Stringify(state), SoloTtl);
                }
            }
            catch (System.Text.Json.JsonException)
            {
            }
        }

        return LobbyDocuments.Ssc([]);
    }

    // ── set_ready_for_lobby (a party lobby; custom lobbies are CustomLobbyService's) ──────────────────────────────────────
    public async Task<JsonObject> SetReadyAsync(PartyRequest request, CancellationToken ct)
    {
        var redis = Redis();
        string me = request.AccountId;
        var body = request.Body;
        var matchIdNode = (body?["MatchID"] is JsonValue m && Truthy(m) ? m : body?["LobbyId"])?.DeepClone();
        string matchId = matchIdNode is JsonValue mi && mi.TryGetValue(out string? text) ? text : Js.Stringify(matchIdNode);
        var ready = body?["Ready"];
        string readyKey = $"party_ready:{matchId}";
        var raw = await redis.StringGetAsync($"lobby:{matchId}");
        JsonObject? state = null;
        try
        {
            state = raw.HasValue ? Js.Parse(raw.ToString()) as JsonObject : null;
        }
        catch (System.Text.Json.JsonException)
        {
        }

        var playerIds = state?["playerIds"] is JsonArray ids ? ids.Select(n => n?.ToString() ?? "").ToList() : null;
        bool readying = ready is JsonValue r && Truthy(r);
        bool riftLobby = Str(state, "mode") == Rifts.RiftLobbyService.Mode;
        if (readying && !riftLobby && await gate.BlockOutdatedAsync([me, .. playerIds ?? []], log, $"ready in party lobby {matchId}"))
        {
            return gate.FailureBody();
        }

        if (readying)
        {
            await redis.SetAddAsync(readyKey, me);
        }
        else
        {
            await redis.KeyDeleteAsync(readyKey);
        }

        await redis.KeyExpireAsync(readyKey, SoloTtl);
        int total = playerIds is { Count: > 0 } ? playerIds.Count : 1;
        long readyCount = await redis.SetLengthAsync(readyKey);
        bool allReady = readyCount >= total;
        log.LogInformation("set_ready_for_lobby: Player {Player} ready={Ready} in party lobby {Lobby} ({Count}/{Total}, allReady={All})", me, Js.Stringify(ready), matchId, readyCount, total, allReady);

        if (state is not null)
        {
            var targets = (playerIds ?? []).Where(p => p != me).ToList();
            JsonObject Notice(JsonNode? readyValue, bool all) => PlayerMessages.Update(new JsonObject
            {
                ["template_id"] = "PlayerReadyForLobby",
                ["MatchID"] = matchIdNode?.DeepClone(),
                ["PlayerID"] = me,
                ["Ready"] = readyValue?.DeepClone(),
                ["bAllPlayersReady"] = all,
            }, new JsonObject { ["match"] = new JsonObject { ["id"] = matchIdNode?.DeepClone() } });
            await PlayerMessages.SendAsync(redis, targets, Notice(ready, false));
            if (allReady)
            {
                await PlayerMessages.SendAsync(redis, targets, Notice(true, true));
            }
        }

        var answer = new JsonObject { ["MatchID"] = matchIdNode?.DeepClone(), ["PlayerID"] = me };
        if (ready is not null)
        {
            answer["Ready"] = ready.DeepClone();
        }

        answer["bAllPlayersReady"] = allReady;
        return LobbyDocuments.Ssc(answer);
    }

    // ── lock_lobby_loadout (a party lobby; custom lobbies are CustomLobbyService's) ───────────────────────────────────────
    public async Task<JsonObject> LockLoadoutAsync(PartyRequest request, CancellationToken ct)
    {
        var redis = Redis();
        string me = request.AccountId;
        var loadout = request.Body?["Loadout"] as JsonObject;
        string character = Str(loadout, "Character") ?? "";
        string skin = Str(loadout, "Skin") ?? "";

        // The match's copy of the player's cosmetics: the one there, else the equipped ones.
        var cached = await HashAsync(redis, $"connections:{me}:cosmetics");
        JsonObject equipped;
        if (cached.Count > 0)
        {
            equipped = [];
            foreach (var (field, value) in cached)
            {
                try
                {
                    equipped[field] = Js.Parse(value);
                }
                catch (System.Text.Json.JsonException)
                {
                    equipped[field] = value;
                }
            }
        }
        else
        {
            equipped = await cosmetics.EquippedAsync(me, ct);
        }

        bool known = await PlayerRecordAsync(me, ct) is not null;
        if (known)
        {
            await cosmetics.WriteMatchCopyAsync(me, equipped);
        }

        if (!known || s_disabledAtLock.Contains(character))
        {
            log.LogWarning("Refused lobby loadout of {Player}: {Character}{Why}", me, character, known ? "" : " (no player record)");
            return Locked(me, character, skin, false);
        }

        await redis.HashSetAsync($"player:{me}", [new("character", character), new("skin", skin), new("ip", LoadoutIp(request.ClientIp))]);
        if (services.GetService<IMongoDatabase>() is { } mongo && ObjectId.TryParse(me, out var oid))
        {
            await mongo.GetCollection<BsonDocument>("playertesters").UpdateOneAsync(new BsonDocument("_id", oid),
                new BsonDocument("$set", new BsonDocument { ["character"] = character, ["variant"] = skin }), cancellationToken: ct);
        }

        if (await redis.StringGetAsync($"player_lobby:{me}") is { IsNullOrEmpty: false } partyId
            && await LobbyAsync(redis, partyId!) is { PlayerIds.Count: > 1 } party)
        {
            var others = party.PlayerIds.Where(p => p != me).ToList();
            string lobbyId = partyId!;
            // After the answer: the game shows the lock first, then the other players' loadouts again (its answer
            // handling clears them from the view).
            After(LockNoticeDelay, async () =>
            {
                foreach (string other in others)
                {
                    await PlayerMessages.SendAsync(redis, [other], LoadoutLocked(lobbyId, me, character, skin));
                }

                foreach (string other in others)
                {
                    var theirs = await HashAsync(redis, $"player:{other}");
                    var session = await HashAsync(redis, $"connections:{other}");
                    await PlayerMessages.SendAsync(redis, [me], LoadoutLocked(lobbyId, other,
                        Or(Get(theirs, "character"), Get(session, "character"), "character_shaggy"), Or(Get(theirs, "skin"), Get(session, "skin"), "skin_shaggy_default")));
                }
            });
        }

        return Locked(me, character, skin, true);
    }

    private static JsonObject Locked(string me, string character, string skin, bool locked) => LobbyDocuments.Ssc(new JsonObject
    {
        ["AccountId"] = me,
        ["Loadout"] = LobbyDocuments.Loadout(character, skin),
        ["bAreAllLoadoutsLocked"] = locked,
    });

    private static JsonObject LoadoutLocked(string lobbyId, string playerId, string character, string skin) => PlayerMessages.Update(new JsonObject
    {
        ["template_id"] = "OnPlayerLoadoutLocked",
        ["LobbyId"] = lobbyId,
        ["AccountId"] = playerId,
        ["Loadout"] = LobbyDocuments.Loadout(character, skin),
        ["bAreAllLoadoutsLocked"] = true,
    });

    // ── shared ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A lobby:{id} value: the TS server's fields, and any others kept as they were read.</summary>
    private sealed class LobbyState(string id, string ownerId, string ownerUsername, string mode, List<string> playerIds, JsonNode? createdAt, JsonObject? read)
    {
        public string Id { get; } = id;
        public string OwnerId { get; set; } = ownerId;
        public string Mode { get; set; } = mode;
        public List<string> PlayerIds { get; } = playerIds;
        public JsonNode? CreatedAt { get; } = createdAt;

        public string ToJson()
        {
            var json = read?.DeepClone().AsObject() ?? new JsonObject
            {
                ["lobbyId"] = Id,
                ["ownerId"] = OwnerId,
                ["ownerUsername"] = ownerUsername,
                ["mode"] = Mode,
                ["playerIds"] = null,
                ["createdAt"] = CreatedAt?.DeepClone(),
            };
            json["ownerId"] = OwnerId;
            json["mode"] = Mode;
            json["playerIds"] = new JsonArray(PlayerIds.Select(p => (JsonNode?)p).ToArray());
            return Js.Stringify(json);
        }

        /// <summary>When the lobby was made (whole seconds); a createdAt that is not a number is the TS server's NaN date, sent as 0.</summary>
        public long CreatedSeconds => CreatedAt is JsonValue v && v.TryGetValue<double>(out double ms) && double.IsFinite(ms) ? (long)Math.Floor(ms / 1000) : 0;
    }

    private static async Task<LobbyState?> LobbyAsync(IDatabase redis, string lobbyId)
    {
        if (await redis.StringGetAsync($"lobby:{lobbyId}") is not { HasValue: true } raw)
        {
            return null;
        }

        JsonObject? json;
        try
        {
            json = Js.Parse(raw.ToString()) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }

        if (json?["playerIds"] is not JsonArray ids)
        {
            return null;
        }

        return new LobbyState(lobbyId, Str(json, "ownerId") ?? "", Str(json, "ownerUsername") ?? "", Str(json, "mode") ?? "",
            ids.Select(n => n is JsonValue v && v.TryGetValue(out string? s) ? s : Js.Stringify(n)).ToList(), json["createdAt"], json);
    }

    // redisSaveLobbyState: 8 h with 2+ players, else 1 h.
    private static Task SaveLobbyAsync(IDatabase redis, LobbyState lobby) =>
        redis.StringSetAsync($"lobby:{lobby.Id}", lobby.ToJson(), lobby.PlayerIds.Count >= 2 ? PartyTtl : SoloTtl);

    // The TS createLobby and the state saved with it: a new solo 1v1 lobby owned by the player.
    private async Task<LobbyState> NewLobbyAsync(IDatabase redis, string owner, Dictionary<string, string> connection)
    {
        string id = ObjectId.GenerateNewId().ToString();
        var now = time.GetUtcNow();
        await redis.HashSetAsync($"player:{owner}:lobby:{id}",
        [
            new("id", id),
            new("created_at", now.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")),
            new("mode", "1v1"),
            new("owner", owner),
        ]);
        await redis.HashSetAsync($"connections:{owner}", "lobby_id", id);
        var lobby = new LobbyState(id, owner, Or(Get(connection, "username"), Get(connection, "hydraUsername"), "Unknown"), "1v1", [owner], now.ToUnixTimeMilliseconds(), null);
        await SaveLobbyAsync(redis, lobby);
        await redis.StringSetAsync($"player_lobby:{owner}", id, PartyTtl);
        log.LogInformation("Creating party lobby for {Player} - matchLobbyId:{Lobby}", owner, id);
        return lobby;
    }

    // Every player's entry, the first joined when the lobby was made and the rest now; their stored loadouts. Optionally
    // (re)writes each player's match copy of their cosmetics, as the rejoin and the join do.
    private async Task<JsonObject> LobbyOfAsync(IDatabase redis, LobbyState lobby, JsonNode? mode, bool writeCosmetics, CancellationToken ct)
    {
        var members = new List<LobbyDocuments.Member>();
        for (int i = 0; i < lobby.PlayerIds.Count; i++)
        {
            string pid = lobby.PlayerIds[i];
            var connection = await HashAsync(redis, $"connections:{pid}");
            var loadout = await HashAsync(redis, $"player:{pid}");
            if (writeCosmetics)
            {
                await cosmetics.WriteMatchCopyAsync(pid, await cosmetics.EquippedAsync(pid, ct));
            }

            members.Add(new LobbyDocuments.Member(pid, i == 0 ? lobby.CreatedSeconds : Now(), Preferences(connection),
                Or(Get(loadout, "character"), "character_shaggy"), Or(Get(loadout, "skin"), "skin_shaggy_default")));
        }

        var answer = LobbyDocuments.Lobby(members, lobby.OwnerId, settings.CurrentValue.GameVersion, "", lobby.Id);
        answer["ModeString"] = mode?.DeepClone();
        return answer;
    }

    // playerIds.length >= 2 ? "2v2" : (mode || "1v1")
    private static string ModeOf(LobbyState lobby) => lobby.PlayerIds.Count >= 2 ? "2v2" : Or(lobby.Mode, "1v1");

    // The IP-keyed copy of the session gets lobby_id only while it is this player's (a household shares an IP).
    private static async Task MirrorLobbyIdAsync(IDatabase redis, string? ip, string playerId, string lobbyId)
    {
        if (!string.IsNullOrEmpty(ip) && (string?)await redis.HashGetAsync($"connections:{ip}", "id") == playerId)
        {
            await redis.HashSetAsync($"connections:{ip}", "lobby_id", lobbyId);
        }
    }

    // The address the matchmaker gives the player's match: loopback is the server's own public address.
    private string LoadoutIp(string ip)
    {
        ip = ip.StartsWith("::ffff:", StringComparison.Ordinal) ? ip["::ffff:".Length..] : ip;
        return ip == "127.0.0.1" && settings.CurrentValue.LocalPublicIp is { Length: > 0 } local ? local : ip;
    }

    private async Task<BsonDocument?> PlayerRecordAsync(string playerId, CancellationToken ct)
    {
        if (services.GetService<IMongoDatabase>() is not { } mongo || !ObjectId.TryParse(playerId, out var oid))
        {
            return null;
        }

        return await mongo.GetCollection<BsonDocument>("playertesters").Find(new BsonDocument("_id", oid)).FirstOrDefaultAsync(ct);
    }

    // Runs work after the answer has gone (the TS server's setTimeout); a failure is logged.
    private void After(TimeSpan delay, Func<Task> work) => _ = Task.Run(async () =>
    {
        try
        {
            await Task.Delay(delay, time);
            await work();
        }
        catch (Exception e)
        {
            log.LogError("Delayed lobby notification failed: {Error}", e.Message);
        }
    });

    private long Now() => time.GetUtcNow().ToUnixTimeSeconds();

    private long NowMs() => time.GetUtcNow().ToUnixTimeMilliseconds();

    private static JsonNode Preferences(Dictionary<string, string> connection) => JsonValue.Create(GameplayPreferences.Of(Get(connection, "GameplayPreferences")));

    private static async Task<Dictionary<string, string>> HashAsync(IDatabase redis, string key) =>
        (await redis.HashGetAllAsync(key)).ToDictionary(e => e.Name.ToString(), e => e.Value.ToString());

    private static string? Get(Dictionary<string, string> hash, string field) => hash.TryGetValue(field, out var value) ? value : null;

    private static string? Str(JsonObject? obj, string key) => obj?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    // A text field of a mongoose document: the schema default when the field is missing, null when it is not text.
    private static string? Field(BsonDocument doc, string key, string schemaDefault) =>
        !doc.TryGetValue(key, out var v) ? schemaDefault : v.IsString ? v.AsString : null;

    // body.a || body.b || ... || "" for strings.
    private static string FirstStr(JsonObject? body, params string[] keys) => keys.Select(k => Str(body, k)).FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? "";

    private static string Or(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? "";

    // JavaScript truthiness of a JSON value.
    private static bool Truthy(JsonValue v) =>
        v.TryGetValue(out bool b) ? b : v.TryGetValue(out double d) ? d != 0 && !double.IsNaN(d) : v.TryGetValue(out string? s) ? s.Length > 0 : true;
}

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
using OpenVersus.Server.Core.Lobbies;
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
//   lobby:{id}                  the lobby (Lobbies/Lobby.cs, read and changed through LobbyStore) (+ joinable false once
//                               not joinable); EX 8 h with 2+ players, else 1 h (the shared leave writes 1 h)
//   player_lobby:{player}       lobby id, EX 8 h (the shared leave writes 1 h)
//   pending_join_lobby:{player} lobby id, EX 60 s: the lobby an invited (or left) player's next join goes to
//   connections:{player}        lobby_id written; the session read for names, GameplayPreferences, character, skin
//   player:{player}             character, skin, ip (+ profileIcon at create_party_lobby): the loadout the matchmaker reads
//   party_ready:{lobby}         set of ready players, EX 1 h
//   connections:{player}:cosmetics  the match's copy of every lobby player's cosmetics (ICosmeticsService.WriteMatchCopyAsync)
//   online_players              read: a party is only rejoined when everyone in it is online
//   dll_notifications:{player}  party_left notices for the OpenVersus client
// When someone joins a party, everyone in it is canceled ("party-changed": MatchmakingQueue); the TS server published
// matchmaking:cancel for its websocket, which kept the queue ticket and its tick.
//
// Client gate: readying up in a party lobby is what the game does before it sends its matchmaking request, and the one
// refusal on that path it backs out of (a refused matchmaking request leaves it waiting for its ticket, cancel disabled).
// So a ready from a party with anyone who must update is refused (BlockOutdatedAsync), each of them toasted; un-readying
// is never refused. A custom lobby's or a rift lobby's ready is not gated: starting its match is (in a rift lobby the game
// does not back out of a refused ready; it waits on its loading screen).
//
// A player whose game is gone leaves their party as the TS websocket's close took them out of it (PlayerDisconnectedAsync,
// then ForgetLobbyAsync), called by the lobbies' reader of the realtime gateway's disconnects (Realtime/LobbyDisconnects.cs).
//
// Differences from the TS server, none on the wire:
//   - lobby_id is written to the session as one field; the TS server read the whole session and wrote it all back,
//     losing a change made in between (a GameplayPreferences update, say).
//   - the IP-keyed session copy (connections:{ip}) gets lobby_id only when it is this player's; the TS check compared
//     the copy's id with itself, so it always wrote, into another household member's copy too.
//   - set_mode_for_lobby no longer reads every session in Redis (KEYS connections:*) for a debug log.
//   - the TS createLobby's record (player:{player}:lobby:{id}: id, created_at, mode, owner; no TTL, deleted with the
//     player's keys when their session ends) is not written: set_mode_for_lobby checks the lobby's own owner, and writes
//     the mode into a party lobby (the TS server wrote it into that record, which nothing read). A solo party lobby that
//     a join makes a duo takes the duo form of its mode (PartyLobby.Join: 1v1 and FFA are 2v2, ranked 1v1 is ranked 2v2):
//     the TS server's PUT /matches join sent such a duo 1v1, and its join_party_lobby 2v2 whatever the mode.
//   - a login's new party lobby starts in 2v2 (PartyLobby.LoginMode; the TS server's 1v1). A player who leaves a lobby,
//     or is left alone in one, keeps the mode it had: the TS server put them back in 1v1.
//   - set_lobby_not_joinable keeps the lobby's lifetime and set_lobby_joinable marks it joinable again (both below).
//   - an un-ready takes back only that player's ready; the TS server deleted the whole party's (party_ready:{lobby}), so
//     the other player, still shown ready, had to ready again before the party could be all ready. While a party lobby of
//     two players is searching (either of them holds a matchmaking ticket), either player's un-ready cancels the search
//     (MatchmakingQueue.CancelAsync: both games are told matchmaking-cancel) and un-readies both; the TS server left the
//     ticket searching.
//   - party keys (the retired /party page) are not updated: nothing makes them any more.
//   - a mode change is sent to everyone in the party; the TS server told only the player who changed it.
//   - an invite that names no lobby is not sent (it could not be accepted); the TS server sent it with an empty MatchID.
//   - a loadout lock with a disabled character, or from a player with no record, is answered (bAreAllLoadoutsLocked
//     false, as the rift lock answers); the TS server never answered it.
//   - a player with no session (connections:{player}) is still the player their token names: their cosmetics go into
//     the match copy, and their lock is recorded and answered. The TS server looked the session's id up, found none,
//     wrote the cosmetics of "undefined" (player:undefined:cosmetics, shared by every such player) and, for a lock,
//     never answered.
//   - the solo lobby a leave makes shows the player's fighter as their lobby has it (player:{player}, which the lobby's
//     creation and every loadout lock write), then the session's, then Shaggy; the TS server read the session only,
//     which has a fighter only once a matchmaking request has written one, so leaving a lobby before queueing turned
//     the player into Shaggy (seen leaving an Arena lobby, 2026-10-10).

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
    Task<JsonObject> SetJoinableAsync(PartyRequest request, CancellationToken ct = default);
    Task<JsonObject> SetReadyAsync(PartyRequest request, CancellationToken ct = default);
    Task<JsonObject> LockLoadoutAsync(PartyRequest request, CancellationToken ct = default);

    /// <summary>
    /// The player's game is gone (LobbyDisconnects): a party of two loses them. The other is told (PlayerLeftLobby) and
    /// the ready set goes; the owner gone, the lobby goes and the other gets a solo lobby to join; the other gone, the
    /// owner keeps the lobby, alone. Nothing for a solo lobby (<see cref="ForgetLobbyAsync"/>).
    /// </summary>
    Task PlayerDisconnectedAsync(string playerId);

    /// <summary>After a disconnect: the player's player_lobby goes, and their place in that lobby (the lobby too, when it is left empty).</summary>
    Task ForgetLobbyAsync(string playerId);
}

internal sealed class PartyService(IServiceProvider services, ICosmeticsService cosmetics, IFunFacts funFacts, IClientUpdateGate gate, IOptionsMonitor<LobbySettings> settings,
    TimeProvider time, ILogger<PartyService> log) : IPartyService
{

    // The TS server's delays before telling the other players, after its answer: the game handles the answer first.
    internal static TimeSpan JoinNoticeDelay = TimeSpan.FromMilliseconds(500);
    internal static TimeSpan LockNoticeDelay = TimeSpan.FromMilliseconds(200);

    private static readonly TimeSpan PartyTtl = LobbyStore.PartyTtl;
    private static readonly TimeSpan SoloTtl = LobbyStore.SoloTtl;

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
        if (await LobbyStore.PointerAsync(redis, me) is { } existingId
            && await LobbyStore.GetAsync(redis, existingId) is { } existing && existing.PlayerIds.Count > 1 && existing.PlayerIds.Contains(me))
        {
            var online = (await redis.SetMembersAsync("online_players")).Select(v => v.ToString()).ToHashSet();
            if (existing.PlayerIds.Where(p => p != me).All(online.Contains))
            {
                log.LogInformation("REJOIN: Player {Player} is already in multi-player lobby {Lobby}, returning existing lobby data", me, existingId);
                existing = await LobbyStore.UpdateAsync(redis, existingId, _ => LobbyWrite.Save) ?? existing;
                foreach (string pid in existing.PlayerIds)
                {
                    await LobbyStore.SetPointerAsync(redis, pid, existing.Id, PartyTtl);
                }

                return LobbyDocuments.Answer(await LobbyOfAsync(redis, existing, ModeOf(existing), writeCosmetics: true, ct));
            }

            log.LogInformation("REJOIN SKIPPED: Lobby {Lobby} has offline players, cleaning up stale data", existingId);
            await LobbyStore.UpdateAsync(redis, existingId, lobby =>
            {
                lobby.PlayerIds.Remove(me);
                return lobby.PlayerIds.Count == 0 ? LobbyWrite.Delete : LobbyWrite.Save;
            });
            await LobbyStore.ClearPointerAsync(redis, me);
        }

        var lobby = await NewLobbyAsync(redis, me, connection, PartyLobby.LoginMode);
        var member = new LobbyDocuments.Member(me, Now(), Preferences(connection), shownCharacter, shownSkin);
        return LobbyDocuments.Answer(LobbyDocuments.Lobby([member], me, settings.CurrentValue.GameVersion, lobby.Mode, lobby.Id));
    }

    // ── create_party (PartyManager::CreateParty: a flat {MatchID}) ───────────────────────────────────────────────────
    public async Task<JsonObject> CreatePartyAsync(PartyRequest request, CancellationToken ct)
    {
        var redis = Redis();
        string me = request.AccountId;
        string? lobbyId = await LobbyStore.PointerAsync(redis, me);
        if (lobbyId is null)
        {
            lobbyId = (await NewLobbyAsync(redis, me, await HashAsync(redis, $"connections:{me}"), PartyLobby.LoginMode)).Id;
            log.LogInformation("create_party: Created new lobby {Lobby} for player {Player}", lobbyId, me);
        }

        return LobbyDocuments.Ssc(new JsonObject { ["MatchID"] = lobbyId });
    }

    // ── set_mode_for_lobby ──────────────────────────────────────────────────────────────────────────────────────────
    public async Task<JsonObject> SetModeAsync(PartyRequest request, CancellationToken ct)
    {
        var redis = Redis();
        string me = request.AccountId;
        if (await LobbyStore.PointerAsync(redis, me) is not { } lobbyId)
        {
            log.LogWarning("set_lobby_mode: No lobby found in Redis for player {Player}", me);
            return [];
        }

        var mode = request.Body?["ModeString"]?.DeepClone();
        string modeText = mode is JsonValue mv && mv.TryGetValue(out string? s) ? s : Js.Stringify(mode);

        // changeLobbyMode: only the lobby's owner changes its mode. A party lobby keeps it (a join's lobby then shows it); a
        // rift or Arena lobby's mode is its kind, so it is not overwritten.
        var lobby = await LobbyStore.GetAsync(redis, lobbyId);
        if (lobby is null)
        {
            log.LogError("Lobby not found for id: {Lobby}", lobbyId);
        }
        else if (lobby.OwnerId != me)
        {
            log.LogError("You are not the owner of this lobby");
        }
        else
        {
            if (lobby is PartyLobby)
            {
                lobby = await LobbyStore.UpdateAsync(redis, lobbyId, changed =>
                {
                    changed.Mode = modeText;
                    return LobbyWrite.Save;
                }) ?? lobby;
            }

            log.LogInformation("set_mode_for_lobby: {Player} set lobby {Lobby} to {Mode}", me, lobbyId, modeText);
            // Everyone in the party hears of it (the TS server told only the player who changed it).
            var party = lobby.PlayerIds.Contains(me) ? lobby.PlayerIds : [me];
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

        if (await LobbyStore.GetAsync(redis, lobbyId) is { PlayerIds.Count: > 1 } full)
        {
            log.LogInformation("set_lobby_mode: Lobby {Lobby} has {Count} players, returning full lobby data", lobbyId, full.PlayerIds.Count);
            return LobbyDocuments.Answer(await LobbyOfAsync(redis, full, mode, writeCosmetics: false, ct));
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
        if (await LobbyStore.GetAsync(redis, lobbyId) is { } lobby)
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
        string? mapped = await LobbyStore.PointerAsync(redis, me);
        string? fromBody = new[] { "LobbyId", "lobbyId", "MatchID", "matchId" }.Select(k => Str(body, k)).FirstOrDefault(v => !string.IsNullOrEmpty(v));
        // An accepted invite (pending) wins over the player's own lobby, which wins over what the game sends.
        string? target = new[] { pending, mapped, fromBody }.FirstOrDefault(v => !string.IsNullOrEmpty(v));
        log.LogInformation("join_party_lobby: Player {Player}: pending={Pending}, body={Body}, redis={Mapped}", me, pending, fromBody, mapped);
        if (target is null)
        {
            log.LogWarning("join_party_lobby: No lobby ID found for player {Player}", me);
            return LobbyDocuments.Ssc([], returnCode: 200);
        }

        if (await LobbyStore.GetAsync(redis, target) is not { } lobby)
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
            await LobbyStore.UpdateAsync(redis, target, stale =>
            {
                stale.PlayerIds.Clear();
                stale.PlayerIds.Add(me);
                stale.OwnerId = me;
                return LobbyWrite.Save;
            });
            await LobbyStore.SetPointerAsync(redis, me, target, PartyTtl);
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
        }

        lobby = await LobbyStore.UpdateAsync(redis, target, joined =>
        {
            if (joined.PlayerIds.Contains(me))
            {
                return LobbyWrite.Keep;
            }

            joined.Join(me);
            return LobbyWrite.Save;
        }) ?? lobby;
        await LobbyStore.SetPointerAsync(redis, me, target, PartyTtl);
        if (!string.IsNullOrEmpty(pending))
        {
            await redis.KeyDeleteAsync($"pending_join_lobby:{me}");
        }

        var answerLobby = await LobbyOfAsync(redis, lobby, ModeOf(lobby), writeCosmetics: true, ct);
        // Only a player joining someone else's party is announced (the owner's own join would loop).
        if (me != lobby.OwnerId)
        {
            await MatchmakingQueue.CancelAsync(redis, lobby.PlayerIds, "party-changed");
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
                    await LobbyStore.SetPointerAsync(redis, other, target, PartyTtl);
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
        if (Str(request.Body, "LobbyId") is { } named && await LobbyStore.GetAsync(redis, named) is { } left && left.PlayerIds.Contains(me))
        {
            log.LogInformation("leave_player_lobby: Player {Player} leaving party lobby {Lobby}", me, named);
            var remaining = (await LobbyStore.UpdateAsync(redis, named, lobby =>
            {
                lobby.PlayerIds.Remove(me);
                return lobby.PlayerIds.Count == 0 ? LobbyWrite.Delete : LobbyWrite.SaveFor(SoloTtl);
            }))?.PlayerIds ?? [];

            await LobbyStore.ClearPointerAsync(redis, me);
            await LobbyStore.ResetReadyAsync(redis, named);
            if (remaining.Count > 0)
            {
                await PlayerMessages.SendAsync(redis, remaining, PlayerLeftNotice(named, me, remaining[0]));
            }

            string soloId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));
            var connection = await HashAsync(redis, $"connections:{me}");
            var loadout = await HashAsync(redis, $"player:{me}");
            // The mode they last played stays theirs.
            string kept = Or(left.Mode, "1v1");
            await LobbyStore.SaveAsync(redis, new PartyLobby(soloId, me, Or(Get(connection, "username"), Get(connection, "hydraUsername"), "Unknown"), kept, [me], NowMs()), SoloTtl);
            await LobbyStore.SetPointerAsync(redis, me, soloId, SoloTtl);
            var member = new LobbyDocuments.Member(me, Now(), Preferences(connection),
                Or(Get(loadout, "character"), Get(connection, "character"), "character_shaggy"), Or(Get(loadout, "skin"), Get(connection, "skin"), "skin_shaggy_default"));
            return LobbyDocuments.Answer(LobbyDocuments.Lobby([member], me, "local", kept, soloId));
        }

        // Otherwise the player's own lobby: a step of joining another (pending join), or a real leave.
        if (await LobbyStore.PointerAsync(redis, me) is not { } lobbyId)
        {
            log.LogInformation("leave_player_lobby: Player {Player} not in any lobby, returning OK", me);
            return LobbyDocuments.Ssc([]);
        }

        if (await LobbyStore.GetAsync(redis, lobbyId) is not { } lobby)
        {
            log.LogWarning("leave_player_lobby: Lobby {Lobby} not found, cleaning stale mapping", lobbyId);
            await LobbyStore.ClearPointerAsync(redis, me);
            return LobbyDocuments.Ssc([]);
        }

        if (await redis.StringGetAsync($"pending_join_lobby:{me}") is { IsNullOrEmpty: false } pendingJoin)
        {
            log.LogInformation("leave_player_lobby: JOIN TRANSITION for {Player} (pending_join={Pending})", me, (string?)pendingJoin);
            await LobbyStore.ClearPointerAsync(redis, me);
            await LobbyStore.UpdateAsync(redis, lobbyId, left =>
            {
                left.PlayerIds.Remove(me);
                // An empty lobby the player is about to rejoin is kept for the join.
                return left.PlayerIds.Count > 0 || (string?)pendingJoin == lobbyId ? LobbyWrite.Save : LobbyWrite.Delete;
            });
            return LobbyDocuments.Ssc([]);
        }

        await GenuineLeaveAsync(redis, me, lobby);
        return LobbyDocuments.Ssc([]);
    }

    // performGenuineLeave: the owner keeps the lobby, everyone else gets a new solo lobby; the OpenVersus client is told
    // (party_left) and rejoins.
    private async Task GenuineLeaveAsync(IDatabase redis, string leaving, Lobby lobby)
    {
        if (lobby.PlayerIds.Count <= 1)
        {
            await LobbyStore.ClearPointerAsync(redis, leaving);
            await LobbyStore.DeleteAsync(redis, lobby.Id);
            log.LogInformation("genuineLeave: Solo lobby {Lobby} deleted", lobby.Id);
            return;
        }

        string owner = lobby.OwnerId;
        var nonOwners = lobby.PlayerIds.Where(p => p != owner).ToList();
        await LobbyStore.UpdateAsync(redis, lobby.Id, kept =>
        {
            owner = kept.OwnerId;
            nonOwners = kept.PlayerIds.Where(p => p != kept.OwnerId).ToList();
            kept.PlayerIds.Clear();
            kept.PlayerIds.Add(kept.OwnerId);
            return LobbyWrite.Save;
        });
        await redis.StringSetAsync($"pending_join_lobby:{owner}", lobby.Id, TimeSpan.FromSeconds(60));
        await PlayerMessages.NotifyClientAsync(redis, owner, "party_left", "Party Update", owner == leaving ? "Returning to solo lobby" : "Your party member left",
            new JsonObject { ["newLobbyId"] = lobby.Id }, NowMs());

        foreach (string pid in nonOwners)
        {
            var solo = await NewLobbyAsync(redis, pid, await HashAsync(redis, $"connections:{pid}"), Or(lobby.Mode, "1v1"));
            await redis.StringSetAsync($"pending_join_lobby:{pid}", solo.Id, TimeSpan.FromSeconds(60));
            await PlayerMessages.NotifyClientAsync(redis, pid, "party_left", "Party Update", pid == leaving ? "Returning to solo lobby" : "Your party member left",
                new JsonObject { ["newLobbyId"] = solo.Id }, NowMs());
        }
    }

    // A party member left (leave_player_lobby, a disconnect): to the players left, with who leads now. The payload's keys
    // and the last two come in this order here (shared.routes.ts and the websocket's close alike), not Update's.
    private JsonObject PlayerLeftNotice(string lobbyId, string playerId, string newLeader) => new()
    {
        ["data"] = new JsonObject
        {
            ["MatchID"] = lobbyId,
            ["template_id"] = "PlayerLeftLobby",
            ["Player"] = new JsonObject
            {
                ["Account"] = new JsonObject { ["id"] = playerId },
                ["LobbyPlayerIndex"] = 0,
                ["JoinedAt"] = LobbyDocuments.Date(Now()),
                ["BotSettingSlug"] = "",
                ["CrossplayPreference"] = 1,
            },
            ["ReadyPlayers"] = new JsonObject(),
            ["NewLeader"] = newLeader,
        },
        ["payload"] = new JsonObject { ["custom_notification"] = "realtime", ["match"] = new JsonObject { ["id"] = lobbyId } },
        ["cmd"] = "update",
        ["header"] = "",
    };

    // ── A disconnect (LobbyDisconnects; the TS websocket's handleDisconnect) ─────────────────────────────────────────────

    // The party disband of the TS close (websocket.ts 422-509). Unlike a leave (GenuineLeaveAsync), the owner who is gone
    // does not keep the lobby, and no party_left notice is pushed: the other game is told PlayerLeftLobby.
    public async Task PlayerDisconnectedAsync(string playerId)
    {
        var redis = Redis();
        if (await LobbyStore.PointerAsync(redis, playerId) is not { } lobbyId
            || await LobbyStore.GetAsync(redis, lobbyId) is not { } lobby || lobby.PlayerIds.Count <= 1)
        {
            return;
        }

        var others = lobby.PlayerIds.Where(p => p != playerId).ToList();
        if (others.Count == 0)
        {
            // The player twice and nobody else: a solo lobby (ForgetLobbyAsync). TS told nobody, NewLeader undefined.
            return;
        }

        log.LogInformation("Player {Player} disconnected from party lobby {Lobby}: disbanding it; remaining: [{Others}]", playerId, lobbyId, string.Join(", ", others));
        await PlayerMessages.SendAsync(redis, others, PlayerLeftNotice(lobbyId, playerId, others[0]));
        await LobbyStore.ResetReadyAsync(redis, lobbyId);
        if (playerId == lobby.OwnerId)
        {
            await LobbyStore.DeleteAsync(redis, lobbyId);
            await LobbyStore.ClearPointerAsync(redis, playerId);
            foreach (string pid in others)
            {
                // Each on its own, as there: one player's failure does not cost the others their lobby.
                try
                {
                    var solo = await NewLobbyAsync(redis, pid, await HashAsync(redis, $"connections:{pid}"), Or(lobby.Mode, "1v1"));
                    await redis.StringSetAsync($"pending_join_lobby:{pid}", solo.Id, TimeSpan.FromSeconds(60));
                }
                catch (Exception e) when (e is RedisException or TimeoutException)
                {
                    log.LogError("Creating a solo lobby for {Player} after {Owner} disconnected: {Error}", pid, playerId, e.Message);
                }
            }
        }
        else
        {
            // Every other member goes, not only this one: a party has two (InviteAsync refuses a third).
            await LobbyStore.UpdateAsync(redis, lobbyId, kept =>
            {
                kept.PlayerIds.Clear();
                kept.PlayerIds.Add(kept.OwnerId);
                return LobbyWrite.Save;
            });
            await LobbyStore.ClearPointerAsync(redis, playerId);
            await redis.StringSetAsync($"pending_join_lobby:{lobby.OwnerId}", lobbyId, TimeSpan.FromSeconds(60));
        }
    }

    // redisCleanupPlayerLobby (websocket.ts 690). Its lobby_redirect:{lobby} DEL is not ported: nothing writes that key
    // (redisSaveLobbyRedirect has no caller).
    public async Task ForgetLobbyAsync(string playerId)
    {
        var redis = Redis();
        if (await LobbyStore.PointerAsync(redis, playerId) is not { } lobbyId)
        {
            return;
        }

        await LobbyStore.ClearPointerAsync(redis, playerId);
        await LobbyStore.UpdateAsync(redis, lobbyId, lobby =>
        {
            lobby.PlayerIds.RemoveAll(p => p == playerId);
            return lobby.PlayerIds.Count == 0 ? LobbyWrite.Delete : LobbyWrite.Save;
        });

        log.LogInformation("Cleaned up lobby data for disconnected player {Player}", playerId);
    }

    // ── set_lobby_not_joinable / set_lobby_joinable ─────────────────────────────────────────────────────────────────
    // The game marks its lobby not joinable when matchmaking starts or it goes into training mode, and joinable again
    // after. The flag is recorded (joinable false, then true); nothing on this server reads it yet. The lobby keeps its
    // lifetime (8 h with 2+ players): the TS server wrote it back for 1 h, so a party in training mode for over an hour
    // lost its lobby, and its set_lobby_joinable kept nothing.
    public Task<JsonObject> SetNotJoinableAsync(PartyRequest request, CancellationToken ct) => MarkJoinableAsync(request, false);

    public Task<JsonObject> SetJoinableAsync(PartyRequest request, CancellationToken ct) => MarkJoinableAsync(request, true);

    private async Task<JsonObject> MarkJoinableAsync(PartyRequest request, bool joinable)
    {
        if (Str(request.Body, "LobbyId") is { Length: > 0 } lobbyId)
        {
            await LobbyStore.UpdateAsync(Redis(), lobbyId, lobby =>
            {
                lobby.SetField("joinable", joinable);
                return LobbyWrite.Save;
            });
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
        string readyKey = LobbyStore.ReadyKey(matchId);
        var lobby = await LobbyStore.GetAsync(redis, matchId);
        var playerIds = lobby?.PlayerIds;
        bool readying = ready is JsonValue r && Truthy(r);
        bool riftLobby = lobby is RiftLobby;
        if (readying && !riftLobby && await gate.BlockOutdatedAsync([me, .. playerIds ?? []], log, $"ready in party lobby {matchId}"))
        {
            return gate.FailureBody();
        }

        if (readying)
        {
            await redis.SetAddAsync(readyKey, me);
        }
        else if (lobby is PartyLobby { PlayerIds.Count: 2 } && await MatchmakingQueue.HeldTicketAsync(redis, lobby.PlayerIds) is { } ticket)
        {
            // TODO: remove this branch. The game has no un-ready while a search is running, only Cancel (leader and partner
            // alike send MatchmakingRequestService.CancelAsync's route; bench, 2026-10-10), so no game reaches it.
            // A party lobby of two players that is searching: either player's un-ready cancels the search and un-readies both.
            log.LogInformation("set_ready_for_lobby: Player {Player} un-readied while party lobby {Lobby} was searching: search cancelled", me, matchId);
            await MatchmakingQueue.CancelAsync(redis, lobby.PlayerIds, MatchmakingQueue.RequestIdOf(ticket));
            await redis.KeyDeleteAsync(readyKey);
        }
        else
        {
            await redis.SetRemoveAsync(readyKey, me);
        }

        await redis.KeyExpireAsync(readyKey, SoloTtl);
        int total = playerIds is { Count: > 0 } ? playerIds.Count : 1;
        long readyCount = await redis.SetLengthAsync(readyKey);
        bool allReady = readyCount >= total;
        log.LogInformation("set_ready_for_lobby: Player {Player} ready={Ready} in party lobby {Lobby} ({Count}/{Total}, allReady={All})", me, Js.Stringify(ready), matchId, readyCount, total, allReady);

        if (lobby is not null)
        {
            var targets = lobby.PlayerIds.Where(p => p != me).ToList();
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

        if (await LobbyStore.PointerAsync(redis, me) is { } lobbyId
            && await LobbyStore.GetAsync(redis, lobbyId) is { PlayerIds.Count: > 1 } party)
        {
            var others = party.PlayerIds.Where(p => p != me).ToList();
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

    // The TS createLobby and the state saved with it: a new solo lobby owned by the player, in <paramref name="mode"/>.
    private async Task<Lobby> NewLobbyAsync(IDatabase redis, string owner, Dictionary<string, string> connection, string mode)
    {
        string id = ObjectId.GenerateNewId().ToString();
        var now = time.GetUtcNow();
        await redis.HashSetAsync($"connections:{owner}", "lobby_id", id);
        var lobby = new PartyLobby(id, owner, Or(Get(connection, "username"), Get(connection, "hydraUsername"), "Unknown"), mode, [owner], now.ToUnixTimeMilliseconds());
        await LobbyStore.SaveAsync(redis, lobby);
        await LobbyStore.SetPointerAsync(redis, owner, id, PartyTtl);
        log.LogInformation("Creating party lobby for {Player} - matchLobbyId:{Lobby}", owner, id);
        return lobby;
    }

    // Every player's entry, the first joined when the lobby was made and the rest now; their stored loadouts. Optionally
    // (re)writes each player's match copy of their cosmetics, as the rejoin and the join do.
    private async Task<JsonObject> LobbyOfAsync(IDatabase redis, Lobby lobby, JsonNode? mode, bool writeCosmetics, CancellationToken ct)
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

    // The lobby's mode; a duo's in its duo form (a lobby stored before PartyLobby.Join made it so). The TS server said 2v2
    // for any duo.
    private static string ModeOf(Lobby lobby) => lobby.PlayerIds.Count >= 2 ? PartyLobby.DuoMode(lobby.Mode) : Or(lobby.Mode, "1v1");

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

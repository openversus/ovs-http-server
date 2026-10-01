using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Matches;
using OpenVersus.Server.Core.Static;
using OpenVersus.Server.Core.Preferences;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Rifts;

// PUT /ssc/invoke/create_rift_lobby: the lobby a rift is played from, made when the player picks a rift, a chapter and a
// difficulty ("Traverse Rift"). Neither the TS server nor any capture answers it. What the answer must hold is read from
// the client's rift lobby parser (the virtual at 0x1429001f0, build f97148ff): body.lobby, and in it RuntimeData (the
// rift's FMvsRiftRuntimeData), RiftConfigSlug and ChapterGuid, each required (the parser fails without it), and
// RiftState (the player's FRiftState), optional. The rest of the lobby is create_party_lobby's (the TS server's
// ssc.ts), so the party lobby code that reads lobby:{id} (PUT /matches/{id}, a friend joining) sees an ordinary lobby.
//
// Redis, read     connections:{player} (GameplayPreferences, username, hydraUsername), player:{player} (character, skin)
// (lock_rift_lobby_loadout: see LockLoadoutAsync)
// Redis, written  lobby:{id} (JSON, as the TS server's create_party_lobby writes it, mode "rift_lobby", plus riftConfigSlug,
//                 chapterGuid, chapterDifficulty) EX 1 h; player_lobby:{player} = id EX 8 h; player:{player}:lobby:{id}
//                 hash (id, created_at, mode, owner); connections:{player} lobby_id = id
// Mongo           the player's rift state (RiftStateService)
//
// RuntimeData is the rift's entry in the player's runtime data (RiftProgressService: a new player's is the frozen
// load_rifts copy with the progress cleared, keeping its bots; see docs/FROZEN-ACCOUNT-DATA.md), with no powerups.

public interface IRiftLobbyService
{
    /// <summary>The create_rift_lobby answer for the player the session token (<paramref name="claims"/>) names.</summary>
    Task<JsonObject> CreateAsync(JsonObject? claims, JsonObject? request, CancellationToken ct);

    /// <summary>The lock_rift_lobby_loadout answer: the player's character and skin for the rift, recorded.</summary>
    Task<JsonObject> LockLoadoutAsync(JsonObject? claims, JsonObject? request, CancellationToken ct);
}

internal sealed class RiftLobbyService(IServiceProvider services, IRiftStateService states, IRiftProgressService progress,
    IOptionsMonitor<LobbySettings> lobbies, TimeProvider time, ILogger<RiftLobbyService> log) : IRiftLobbyService
{
    public const string Mode = "rift_lobby";
    private const string Cluster = "ec2-us-east-1-dokken";

    private static readonly Lazy<JsonObject> s_runtimeData = new(() =>
        JsonNode.Parse(StaticResponses.Json("ssc-load-rifts"))!["body"]!["DynamicInstanceRuntimeData"]!.AsObject());

    public async Task<JsonObject> CreateAsync(JsonObject? claims, JsonObject? request, CancellationToken ct)
    {
        string? playerId = claims?["id"] is JsonValue v && v.TryGetValue(out string? id) && id.Length > 0 ? id : null;
        if (playerId is null || services.GetService<IConnectionMultiplexer>()?.GetDatabase() is not { } redis)
        {
            log.LogWarning("create_rift_lobby with no player id or no Redis; answered empty");
            return new JsonObject { ["body"] = new JsonObject(), ["metadata"] = null, ["return_code"] = 0 };
        }

        string slug = Str(request, "RiftConfigSlug") ?? "";
        string chapter = Str(request, "ChapterGuid") ?? "";
        int difficulty = request?["ChapterDifficulty"] is JsonValue d && d.TryGetValue(out double n) ? (int)n : 0;

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
            ["riftConfigSlug"] = slug,
            ["chapterGuid"] = chapter,
            ["chapterDifficulty"] = difficulty,
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
        log.LogInformation("Created rift lobby {Lobby} for {Player}: {Rift} chapter {Chapter} difficulty {Difficulty}", lobbyId, playerId, slug, chapter, difficulty);

        var teams = new JsonArray(new JsonObject
        {
            ["TeamIndex"] = 0,
            ["Players"] = new JsonObject
            {
                [playerId] = new JsonObject
                {
                    ["Account"] = new JsonObject { ["id"] = playerId },
                    ["JoinedAt"] = new JsonObject { ["_hydra_unix_date"] = now.ToUnixTimeSeconds() },
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

        var lobby = new JsonObject
        {
            ["Teams"] = teams,
            ["LeaderID"] = playerId,
            ["LobbyType"] = request?["LobbyType"]?.DeepClone() ?? 0,
            ["ReadyPlayers"] = new JsonObject(),
            // The value the game sends (the player's current one), else the stored one.
            ["PlayerGameplayPreferences"] = new JsonObject { [playerId] = GameplayPreferences.Parse(request?["GameplayPreferences"]) is { } sent ? JsonValue.Create(sent) : Preferences(connection) },
            ["PlayerAutoPartyPreferences"] = new JsonObject { [playerId] = request?["AutoPartyPreference"]?.DeepClone() ?? false },
            ["GameVersion"] = lobbies.CurrentValue.GameVersion,
            ["HissCrc"] = request?["HissCrc"]?.DeepClone() ?? 0,
            ["Platforms"] = new JsonObject { [playerId] = request?["Platform"]?.DeepClone() ?? "PC" },
            ["AllMultiplayParams"] = request?["AllMultiplayParams"]?.DeepClone() ?? new JsonObject(),
            ["LockedLoadouts"] = new JsonObject
            {
                [playerId] = new JsonObject
                {
                    ["Character"] = Or(Get(player, "character"), "character_shaggy"),
                    ["Skin"] = Or(Get(player, "skin"), "skin_shaggy_default"),
                },
            },
            // A guess: no answer from WB is known, and the rift lobby parser does not read it.
            ["ModeString"] = Mode,
            ["IsLobbyJoinable"] = true,
            ["MatchID"] = lobbyId,
            ["LobbyTemplate"] = Mode,
            ["RiftConfigSlug"] = slug,
            ["ChapterGuid"] = chapter,
            ["ChapterDifficulty"] = difficulty,
            ["RuntimeData"] = RuntimeData((await progress.InstanceAsync(playerId, ct)).Dynamic, slug),
            ["RiftState"] = await states.StateAsync(playerId, ct),
        };

        return new JsonObject
        {
            ["body"] = new JsonObject { ["lobby"] = lobby, ["Cluster"] = Cluster },
            ["metadata"] = null,
            ["return_code"] = 0,
        };
    }

    // PUT /ssc/invoke/lock_rift_lobby_loadout ("Auto equip & fight!"): the request is lock_lobby_loadout's with LobbyTemplate
    // rift_lobby, and so is the answer (the TS server's set_lock_lobby_loadout, ssc.ts): the loadout is recorded as there,
    // Redis player:{player} character, skin and Mongo playertesters {_id} $set character, variant. A character the TS
    // server disables gets bAreAllLoadoutsLocked false (the game then refuses the lock), where the TS server never answers.
    // Nothing is sent to other players yet: a rift lobby has one player until co-op is done.
    public async Task<JsonObject> LockLoadoutAsync(JsonObject? claims, JsonObject? request, CancellationToken ct)
    {
        string? playerId = claims?["id"] is JsonValue v && v.TryGetValue(out string? id) && id.Length > 0 ? id : null;
        string character = Str(request?["Loadout"] as JsonObject, "Character") ?? "";
        string skin = Str(request?["Loadout"] as JsonObject, "Skin") ?? "";
        bool allowed = playerId is not null && character.Length > 0 && !s_disabledCharacters.Contains(character);

        if (allowed)
        {
            if (services.GetService<IConnectionMultiplexer>()?.GetDatabase() is { } redis)
            {
                await redis.HashSetAsync($"player:{playerId}", [new HashEntry("character", character), new HashEntry("skin", skin)]);
            }

            if (services.GetService<IMongoDatabase>() is { } mongo && ObjectId.TryParse(playerId, out var oid))
            {
                await mongo.GetCollection<BsonDocument>("playertesters").UpdateOneAsync(new BsonDocument("_id", oid),
                    new BsonDocument("$set", new BsonDocument { ["character"] = character, ["variant"] = skin }), cancellationToken: ct);
            }

            log.LogInformation("Locked rift loadout of {Player}: {Character} {Skin}", playerId, character, skin);
        }
        else
        {
            log.LogWarning("Refused rift loadout of {Player}: {Character}", playerId ?? "(no id)", character);
        }

        return new JsonObject
        {
            ["body"] = new JsonObject
            {
                ["AccountId"] = playerId ?? "",
                ["Loadout"] = new JsonObject { ["Character"] = character, ["Skin"] = skin },
                ["bAreAllLoadoutsLocked"] = allowed,
            },
            ["metadata"] = null,
            ["return_code"] = 0,
        };
    }

    // The characters the TS server refuses in a lobby loadout (ssc.ts set_lock_lobby_loadout).
    private static readonly HashSet<string> s_disabledCharacters =
        ["character_Meeseeks", "Meeseeks", "character_supershaggy", "supershaggy", "character_c022", "c022", "character_C022", "C022"];

    /// <summary>The rift's runtime data (FMvsRiftRuntimeData) in the frozen load_rifts copy, and no powerups.</summary>
    internal static JsonObject RuntimeData(string slug) => RuntimeData(s_runtimeData.Value, slug);

    /// <summary>The rift's runtime data (FMvsRiftRuntimeData) in a player's DynamicInstanceRuntimeData, and no powerups.</summary>
    internal static JsonObject RuntimeData(JsonObject dynamic, string slug)
    {
        var data = dynamic[slug] is JsonObject entry
            ? entry.DeepClone().AsObject()
            : new JsonObject { ["RuntimeChapterData"] = new JsonObject(), ["RuntimeNodeData"] = new JsonObject() };
        data["Powerups"] = new JsonArray();
        return data;
    }

    // The player's stored value, 0 included; 964 only when there is none, as the party lobby answers.
    private static JsonNode Preferences(Dictionary<string, string> connection) =>
        JsonValue.Create(GameplayPreferences.Of(Get(connection, "GameplayPreferences")));

    private static string? Str(JsonObject? obj, string key) => obj?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static string? Get(Dictionary<string, string> hash, string field) => hash.TryGetValue(field, out var value) ? value : null;

    private static string Or(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? "";
}

using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Cosmetics;
using OpenVersus.Server.Core.Leaderboards;
using OpenVersus.Server.Core.Matchmaking;
using OpenVersus.Server.Core.Perks;
using OpenVersus.Server.Core.Preferences;
using OpenVersus.Server.Core.Settings;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Matches;

// A match's gameplay config, the OnGameplayConfigNotified each player's game plays the match from, ported from the TS
// websocket (websocket.ts, branch infinity-war: handleSendGamePlayConfig, which runs for every match:notifications, and
// handleAllPerksLocked, for every perks:notifications). The TS websocket keeps the config in the memory of each player's
// connection; here it is kept per player in Redis (docs/REALTIME.md), so match end, the perks lock and a disconnect can
// read it from any process.
//
// The config, from the notification (MatchLauncher's, or a TS one) and each player's stores:
//   every player, in the notification's order, into Players, or Spectators for a spectator (TeamIndex 4):
//     a bot: from bot_config:{bot} (character, skin, difficultyMin/Max; Jason and difficulty 2 without), the bot defaults
//       (BotDefaults: perks, icon, banner, trackers worth 0), bUseCharacterDisplayName.
//     a human: the fighter, skin and icon from connections:{player} (character_jason, skin_jason_000 and the gold icon
//       without), GameplayPreferences from there (GameplayPreferences.Of); taunts for that fighter, stat trackers, banner
//       and ring-out effect from the match copy of their cosmetics (connections:{player}:cosmetics, each field JSON; when
//       it is missing, their equipped cosmetics, written back as the match copy with Mode On); each stat tracker's value
//       from playerstats (StatTrackerValues); outside custom games, RankedTier and RankedDivision from their rating for
//       that fighter in the match's mode (else the mode's). Perks empty until the lock.
//       Anything that fails while building a human's config (a stat tracker that is not text, a stored fighter's stats
//       that are null: where the TS code threw) gives them the minimal one TS falls back to: default icon, banner, taunts
//       and ring-out effect, trackers worth 1, no rank; their fighter, skin and GameplayPreferences kept.
//   then the match: 420 s and the mode's ring-outs (2v2 4, else 3) unless the custom game says, hazards from the map
//     (MatchmakingMaps.Hazards) unless it says, shields on, ranked unless isCustomGame, ModeString "ranked-{mode}" when
//     ranked; then gameplayConfigOverride over the config, playerConfigOverrides over those players (not spectators),
//     gameplayConfigTemplate as the template_id, gameplayConfigData over the message's data, in that order. A config with
//     no players after all that is not kept (TS sends nothing).
// The perks lock (perks:notifications {containerMatchId, playerIds}): each human of playerIds that holds this match's
// config gets their locked perks (match:{match}:perks:{player}) into Players in every copy; the players' copies become
// PerksLockedNotification when any perks merged, the spectators' always (TS sends the players theirs before it marks the
// spectators'). Bots keep BotDefaults' perks: TS never merges theirs (a bot has no connection), whatever the launch locked
// for them.
//
// Redis, read     connections:{player}; connections:{player}:cosmetics; bot_config:{bot}; match:{match}:perks:{player};
//                 {match} (who else holds the config: spectators)
// Redis, written  match_config:{player} (the OnGameplayConfigNotified message, for every player but the bots) EX 20 min,
//                 the match's TTL; rewritten at the perks lock, keeping its TTL.
//                 With Mode On, what the TS websocket writes beside it: match_characters:{match} ({player: fighter} for
//                 the players with one in connections:{player}, bots and spectators left out) EX 20 min, and
//                 connections:{player}:cosmetics for a player who had no match copy
// Mongo, read     playerstats {account_id}; eloratings {account_id} (made when missing with Mode On, as getOrCreateRating;
//                 read only in Shadow, a missing one counting as Ranked:DefaultElo, the rating it would be made with);
//                 cosmetics (CosmeticsService.EquippedAsync, for a player with no match copy: that read keeps its own
//                 cache, player:{player}:cosmetics, and makes a missing cosmetics document, as TS does, in either mode)
//
// Who builds it (GameplayConfigs:Mode, cluster setting): Off, the TS websocket alone. Shadow: the match flow builds and
// keeps each config too, writing nothing else the TS server reads, while the TS websocket still sends its own; the two
// are compared (tools/matches/config_diff.mjs on scratch stores; the live bench). On: also the writes the TS websocket
// makes beside it. Nothing here sends: the TS websocket sends the config, or with Realtime:Gateway on, MatchLaunchStream
// (MatchLaunches.cs) builds it with On from the launch and sends it. The
// subscriber below is a bridge (docs/MIGRATION-BRIDGES.md, 9): once the TS websocket is gone, MatchLauncher and the perks
// lock call this directly, as everything else that causes a message does. With more than one match flow replica, each
// builds the same config (the same keys, the same values).
//
// Unlike there:
//   the config is kept for every human and spectator of the match, connected or not (TS kept it on the connections it
//     held at that moment): who it reaches is the gateway's to decide.
//   a stat tracker counts the fighter's entries of both modes, 1v1 and 2v2 (summed; the highest damage is the higher):
//     TS spread the two maps into one, so a fighter with an entry in each showed only its 2v2 numbers.
//   a human's perks are merged whether or not their game is connected (TS merged only those it held a socket for, so the
//     others' cards showed no perks for a player who locked and dropped), and only into this match's config (TS merged
//     into whatever config the connection held, a newer match's included); a locked player missing from Players is
//     logged and skipped (TS threw there, half merged, and sent nobody the lock).
//   a bot's difficulty that is not a number is null (TS sent NaN).

/// <summary>Who builds match configs (see the header of GameplayConfigs.cs).</summary>
public enum GameplayConfigMode
{
    /// <summary>The TS websocket alone.</summary>
    Off,

    /// <summary>The match flow too, keeping each config per player (match_config:{player}) and writing nothing else.</summary>
    Shadow,

    /// <summary>As Shadow, and the writes the TS websocket makes beside it (match_characters, the cosmetics match copy, a missing rating).</summary>
    On,
}

public sealed class GameplayConfigSettings
{
    [Description("Who builds the match configs (OnGameplayConfigNotified): Off, the TS websocket alone; Shadow, the match flow also builds and keeps each one per player (match_config:{player}), writing nothing else the TS server reads; On, also what the TS websocket writes beside it (match_characters, the cosmetics match copy, a missing rating). The TS websocket sends the config in every mode; with Realtime:Gateway on, the match flow builds every config with On and sends it itself (MatchLaunches), whatever this says.")]
    public GameplayConfigMode Mode { get; set; } = GameplayConfigMode.Off;
}

public interface IGameplayConfigs
{
    /// <summary>
    /// Builds <paramref name="notification"/>'s config and keeps it per player; with <paramref name="mode"/> On, also the
    /// TS websocket's writes beside it. Returns the message, or null when there is none to keep.
    /// </summary>
    Task<JsonObject?> BuildAsync(JsonObject notification, GameplayConfigMode mode, CancellationToken ct);

    /// <summary>Merges the locked perks of perks:notifications' <paramref name="notification"/> into the kept configs.</summary>
    Task PerksLockedAsync(JsonObject notification, CancellationToken ct);
}

internal sealed class GameplayConfigs(IServiceProvider services, ICosmeticsService cosmetics, EloRatings ratings,
    IOptionsMonitor<RankedSettings> ranked, TimeProvider time, ILogger<GameplayConfigs> log) : IGameplayConfigs
{
    public const string KeyPrefix = "match_config:";
    public const string PerksLockedTemplate = "PerksLockedNotification";
    private static readonly TimeSpan s_ttl = TimeSpan.FromMinutes(20);

    public static string Key(string playerId) => KeyPrefix + playerId;

    public async Task<JsonObject?> BuildAsync(JsonObject n, GameplayConfigMode mode, CancellationToken ct)
    {
        var redis = services.GetService<IConnectionMultiplexer>()?.GetDatabase() ?? throw new InvalidOperationException("this service has no Redis (REDIS)");
        var mongo = services.GetService<IMongoDatabase>() ?? throw new InvalidOperationException("this service has no Mongo (MONGODB_URI)");
        string matchId = Str(n["matchId"]) ?? "";
        string? gameMode = Str(n["mode"]);
        var players = (n["players"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        bool custom = RollbackCallbacks.Truthy(n["isCustomGame"]);

        // Every human's match copy of their cosmetics first, as TS reads them (the equipped ones when there is none).
        var cosmeticsOf = new Dictionary<string, JsonObject>();
        foreach (var player in players.Where(p => !RollbackCallbacks.Truthy(p["isBot"])))
        {
            string id = Str(player["playerId"]) ?? "";
            cosmeticsOf[id] = await MatchCosmeticsAsync(redis, id, mode, ct);
        }

        var playersOut = new JsonObject();
        var spectatorsOut = new JsonObject();
        foreach (var player in players)
        {
            string id = Str(player["playerId"]) ?? "";
            bool spectator = RollbackCallbacks.Truthy(player["isSpectator"]);
            var target = spectator ? spectatorsOut : playersOut;
            JsonNode? teamIndex = spectator ? JsonValue.Create(4) : player["teamIndex"]?.DeepClone();
            JsonNode buffs = n["playerBuffs"] is JsonObject allBuffs && allBuffs[id] is { } own ? own.DeepClone() : new JsonArray();
            target[id] = RollbackCallbacks.Truthy(player["isBot"])
                ? await BotAsync(redis, id, player, teamIndex, buffs, matchId)
                : await HumanAsync(redis, mongo, id, player, teamIndex, buffs, cosmeticsOf.GetValueOrDefault(id) ?? [], custom, gameMode, mode, matchId, ct);
        }

        // A map that is not text: TS threw looking up its hazards (after the players, their writes included), and sent nothing.
        if (n["map"] is not JsonValue mapValue || !mapValue.TryGetValue(out string? map))
        {
            log.LogError("Match {Match}: its notification names no map; no config", matchId);
            return null;
        }

        bool hazards = MatchmakingMaps.Hazards(map, gameMode);
        JsonNode matchTime = n["customMatchTime"]?.DeepClone() ?? 420;
        JsonNode numRingouts = n["customNumRingouts"]?.DeepClone() ?? (gameMode == "2v2" ? 4 : 3);
        var config = new JsonObject
        {
            ["ArenaModeInfo"] = null,
            ["RiftNodeId"] = "",
            ["ScoreEvaluationRule"] = "TargetScoreIsWin",
            ["bIsPvP"] = true,
            ["ScoreAttributionRule"] = "AttributeToAttacker",
            ["MatchDurationSeconds"] = matchTime.DeepClone(),
            ["Created"] = new JsonObject { ["_hydra_unix_date"] = time.GetUtcNow().ToUnixTimeMilliseconds() / 1000 },
            ["EventQueueSlug"] = "",
            ["bModeGrantsProgress"] = true,
            ["TeamData"] = new JsonArray(),
            ["Spectators"] = spectatorsOut,
            ["bIsRanked"] = !custom,
            ["bIsCustomGame"] = n["isCustomGame"]?.DeepClone() ?? false,
            ["Players"] = playersOut,
            ["CustomGameSettings"] = new JsonObject
            {
                ["bHazardsEnabled"] = n["customHazards"]?.DeepClone() ?? hazards,
                ["bShieldsEnabled"] = n["customShields"]?.DeepClone() ?? true,
                ["MatchTime"] = matchTime.DeepClone(),
                ["NumRingouts"] = numRingouts,
            },
            ["HudSettings"] = new JsonObject { ["bDisplayPortraits"] = true, ["bDisplayStocks"] = true, ["bDisplayTimer"] = true },
            ["bIsCasualSpecial"] = false,
            ["bAllowMapHazards"] = hazards,
            ["RiftNodeAttunement"] = "Attunements:None",
            ["CountdownDisplay"] = "CountdownTypes:XvY",
            ["Cluster"] = "ec2-us-east-1-dokken",
            ["WorldBuffs"] = n["worldBuffs"]?.DeepClone() ?? new JsonArray(),
            ["bIsTutorial"] = false,
            ["MatchId"] = matchId,
            ["bIsOnlineMatch"] = true,
        };
        // `${mode}` in TS: a missing mode is "undefined" there.
        string modeText = gameMode ?? (n["mode"] is { } other ? other.ToJsonString() : "undefined");
        if (custom)
        {
            config["ModeString"] = n["mode"]?.DeepClone();
        }
        else
        {
            config["ModeString"] = "ranked-" + modeText;
        }

        config["Map"] = map;
        config["bIsRift"] = false;

        var data = new JsonObject { ["MatchId"] = matchId, ["GameplayConfig"] = config, ["template_id"] = "OnGameplayConfigNotified" };
        var message = new JsonObject
        {
            ["data"] = data,
            ["payload"] = new JsonObject { ["match"] = new JsonObject { ["id"] = matchId }, ["custom_notification"] = "realtime" },
            ["header"] = "",
            ["cmd"] = "update",
        };

        // The notification's overrides, in TS's order (Object.assign: a key there replaces the config's, in place).
        if (RollbackCallbacks.Truthy(n["gameplayConfigOverride"]) && n["gameplayConfigOverride"] is JsonObject configOverride)
        {
            Assign(config, configOverride);
        }

        if (n["playerConfigOverrides"] is JsonObject playerOverrides)
        {
            foreach (var (playerId, fields) in playerOverrides)
            {
                if (config["Players"] is JsonObject overridden && overridden[playerId] is JsonObject entry && fields is JsonObject given)
                {
                    Assign(entry, given);
                }
            }
        }

        if (RollbackCallbacks.Truthy(n["gameplayConfigTemplate"]))
        {
            data["template_id"] = n["gameplayConfigTemplate"]!.DeepClone();
        }

        if (RollbackCallbacks.Truthy(n["gameplayConfigData"]) && n["gameplayConfigData"] is JsonObject configData)
        {
            Assign(data, configData);
        }

        if (mode == GameplayConfigMode.On)
        {
            // Each player's fighter (bots and spectators have none here), kept for the set: a fighter is locked for it.
            var characters = new JsonObject();
            foreach (var player in players.Where(p => !RollbackCallbacks.Truthy(p["isSpectator"])))
            {
                string id = Str(player["playerId"]) ?? "";
                if ((string?)await redis.HashGetAsync($"connections:{id}", "character") is { Length: > 0 } character)
                {
                    characters[id] = character;
                }
            }

            await redis.StringSetAsync($"match_characters:{matchId}", Js.Stringify(characters), s_ttl);
        }

        // As TS counts them, after the overrides: none, and nothing is sent.
        int count = (data["GameplayConfig"] as JsonObject)?["Players"] switch
        {
            JsonObject o => o.Count,
            JsonArray a => a.Count,
            JsonValue v when v.TryGetValue(out string? s) => s.Length,
            _ => 0,
        };
        if (count == 0)
        {
            log.LogError("Match {Match}: its gameplay config has no players; not kept", matchId);
            return null;
        }

        string json = Js.Stringify(message);
        foreach (var player in players.Where(p => !RollbackCallbacks.Truthy(p["isBot"])))
        {
            await redis.StringSetAsync(Key(Str(player["playerId"]) ?? ""), json, s_ttl);
        }

        log.LogInformation("Match {Match}: gameplay config kept for {Players} ({Mode})", matchId,
            string.Join(", ", players.Where(p => !RollbackCallbacks.Truthy(p["isBot"])).Select(p => Str(p["playerId"]))), mode);
        return message;
    }

    public async Task PerksLockedAsync(JsonObject notification, CancellationToken ct)
    {
        var redis = services.GetService<IConnectionMultiplexer>()?.GetDatabase() ?? throw new InvalidOperationException("this service has no Redis (REDIS)");
        string matchId = Str(notification["containerMatchId"]) ?? "";
        var locked = (notification["playerIds"] as JsonArray ?? []).Select(Str).OfType<string>().ToList();
        var matchPlayers = await RollbackCallbacks.JsonAsync(redis, matchId) is JsonObject stored
            ? (stored["players"] as JsonArray ?? []).OfType<JsonObject>().ToList()
            : [];
        var spectators = matchPlayers.Where(p => RollbackCallbacks.Truthy(p["isSpectator"])).Select(p => Str(p["playerId"])).OfType<string>().ToHashSet();

        // The copies of this match's config, by who holds them (a bot holds none; a player whose key now holds a newer
        // match's config is left alone).
        var held = new Dictionary<string, JsonObject>();
        foreach (string id in locked.Concat(matchPlayers.Select(p => Str(p["playerId"])).OfType<string>()).Distinct())
        {
            if (await RollbackCallbacks.JsonAsync(redis, Key(id)) is JsonObject copy && Str(copy["payload"]?["match"]?["id"]) == matchId)
            {
                held[id] = copy;
            }
        }

        if (held.Count == 0)
        {
            log.LogWarning("Match {Match}: perks locked, but no player holds its gameplay config", matchId);
            return;
        }

        bool merged = false;
        foreach (string id in locked.Where(held.ContainsKey))
        {
            JsonNode? perks;
            try
            {
                perks = (string?)await redis.StringGetAsync($"match:{matchId}:perks:{id}") is { } text ? Js.Parse(text) : null;
            }
            catch (JsonException)
            {
                log.LogError("Match {Match}: the perks {Player} locked are not JSON", matchId, id);
                continue;
            }

            // JSON.parse's value, as truthy as TS took it ([] is).
            if (!RollbackCallbacks.Truthy(perks))
            {
                log.LogError("Match {Match}: perks are incomplete for {Player}", matchId, id);
                continue;
            }

            merged = true;
            foreach (var copy in held.Values)
            {
                if (copy["data"]?["GameplayConfig"]?["Players"]?[id] is JsonObject entry)
                {
                    entry["Perks"] = perks!.DeepClone();
                }
                else
                {
                    log.LogError("Match {Match}: {Player} locked perks but is not among its config's players", matchId, id);
                }
            }
        }

        // What each game is sent: a locked player their config, a PerksLockedNotification once any perks merged; a
        // spectator a PerksLockedNotification in any case (TS sends the players theirs before it marks the spectators').
        foreach (var (id, copy) in held)
        {
            bool spectator = spectators.Contains(id);
            if (!spectator && !locked.Contains(id))
            {
                continue;
            }

            if ((merged || spectator) && copy["data"] is JsonObject data)
            {
                data["template_id"] = PerksLockedTemplate;
            }

            await redis.StringSetAsync(Key(id), Js.Stringify(copy), expiry: null, keepTtl: true);
        }

        log.LogInformation("Match {Match}: perks merged into the gameplay config of {Players}", matchId, string.Join(", ", held.Keys));
    }

    // The match copy of a human's cosmetics, each field parsed (kept as text when it is not JSON); their equipped ones
    // when there is none, written back as the match copy with Mode On.
    private async Task<JsonObject> MatchCosmeticsAsync(IDatabase redis, string id, GameplayConfigMode mode, CancellationToken ct)
    {
        var fields = await redis.HashGetAllAsync($"connections:{id}:cosmetics");
        if (fields.Length == 0)
        {
            log.LogWarning("No cosmetics for {Player} in Redis: their equipped ones", id);
            var equipped = await cosmetics.EquippedAsync(id, ct);
            if (mode == GameplayConfigMode.On)
            {
                await cosmetics.WriteMatchCopyAsync(id, equipped);
            }

            return equipped;
        }

        var parsed = new JsonObject();
        foreach (var field in fields)
        {
            string text = field.Value.ToString();
            try
            {
                parsed[field.Name.ToString()] = Js.Parse(text);
            }
            catch (JsonException)
            {
                log.LogError("Cosmetic field {Field} of {Player} is not JSON: kept as text", field.Name.ToString(), id);
                parsed[field.Name.ToString()] = text;
            }
        }

        return parsed;
    }

    private async Task<JsonObject> BotAsync(IDatabase redis, string id, JsonObject player, JsonNode? teamIndex, JsonNode buffs, string matchId)
    {
        var bot = (await redis.HashGetAllAsync($"bot_config:{id}")).ToDictionary(e => e.Name.ToString(), e => e.Value.ToString());
        string character = bot.GetValueOrDefault("character") is { Length: > 0 } c ? c : BotDefaults.Character;
        string skin = bot.GetValueOrDefault("skin") is { Length: > 0 } s ? s : BotDefaults.Skin;
        JsonNode? difficultyMin = JsNumber(Js.Number(bot.GetValueOrDefault("difficultyMin") ?? "2"));
        JsonNode? difficultyMax = JsNumber(Js.Number(bot.GetValueOrDefault("difficultyMax") ?? "2"));
        log.LogInformation("Built bot config for {Bot} (char={Character}, skin={Skin}, diff={Min}-{Max}) for match {Match}", id, character, skin,
            difficultyMin?.ToJsonString(), difficultyMax?.ToJsonString(), matchId);
        return new JsonObject
        {
            ["Taunts"] = new JsonArray("", "", "", ""),
            ["BotBehaviorOverride"] = "",
            ["AccountId"] = id,
            ["bAutoPartyPreference"] = false,
            ["Gems"] = new JsonArray(),
            ["PartyMember"] = null,
            ["GameplayPreferences"] = 0,
            ["BotDifficultyMax"] = difficultyMax,
            ["bIsBot"] = true,
            ["RankedDivision"] = null,
            ["bUseCharacterDisplayName"] = true,
            ["StartingDamage"] = 0,
            ["TeamIndex"] = teamIndex,
            ["ProfileIcon"] = "profile_icon_default_gold",
            ["WinStreak"] = null,
            ["RankedTier"] = null,
            ["Handicap"] = 0,
            ["RingoutVfx"] = "ring_out_vfx_default",
            ["Character"] = character,
            ["Banner"] = "banner_default",
            ["StatTrackers"] = Trackers(0),
            ["Perks"] = BotDefaults.PerksArray(),
            ["PlayerIndex"] = player["playerIndex"]?.DeepClone(),
            ["PartyId"] = player["partyId"]?.DeepClone(),
            ["Username"] = new JsonObject(),
            ["Buffs"] = buffs,
            ["Skin"] = skin,
            ["BotDifficultyMin"] = difficultyMin,
        };
    }

    private async Task<JsonObject> HumanAsync(IDatabase redis, IMongoDatabase mongo, string id, JsonObject player, JsonNode? teamIndex, JsonNode buffs,
        JsonObject playerCosmetics, bool custom, string? gameMode, GameplayConfigMode mode, string matchId, CancellationToken ct)
    {
        var connection = (await redis.HashGetAllAsync($"connections:{id}")).ToDictionary(e => e.Name.ToString(), e => e.Value.ToString());
        string profileIcon = connection.GetValueOrDefault("profileIcon") ?? "profile_icon_default_gold";
        string character = connection.GetValueOrDefault("character") ?? "character_jason";
        string skin = connection.GetValueOrDefault("skin") ?? "skin_jason_000";
        long preferences = GameplayPreferences.Of(connection.GetValueOrDefault("GameplayPreferences"));
        try
        {
            var taunts = Get(Get(playerCosmetics["Taunts"], character), "TauntSlots") is { } slots && RollbackCallbacks.Truthy(slots)
                ? slots.DeepClone()
                : new JsonArray("", "", "", "");
            var trackerSlots = Get(playerCosmetics["StatTrackers"], "StatTrackerSlots") is { } trackers && RollbackCallbacks.Truthy(trackers)
                ? trackers
                : new JsonArray("stat_tracking_bundle_default", "stat_tracking_bundle_default", "stat_tracking_bundle_default");
            var stats = await mongo.GetCollection<BsonDocument>("playerstats").Find(new BsonDocument("account_id", id)).FirstOrDefaultAsync(ct);
            var (tier, division) = custom ? (null, null) : await RankAsync(mongo, id, character, gameMode, mode, ct);
            var statTrackers = new JsonArray();
            for (int i = 0; i < 3; i++)
            {
                var slot = Index(trackerSlots, i);
                statTrackers.Add(new JsonArray(slot?.DeepClone(), StatTrackerValues.Value(slot, stats)));
            }

            return new JsonObject
            {
                ["Taunts"] = taunts,
                ["BotBehaviorOverride"] = "",
                ["AccountId"] = id,
                ["bAutoPartyPreference"] = false,
                ["Gems"] = new JsonArray(),
                ["PartyMember"] = null,
                ["GameplayPreferences"] = preferences,
                ["BotDifficultyMax"] = 0,
                ["bIsBot"] = false,
                ["RankedDivision"] = division,
                ["bUseCharacterDisplayName"] = false,
                ["StartingDamage"] = 0,
                ["TeamIndex"] = teamIndex?.DeepClone(),
                ["ProfileIcon"] = profileIcon,
                ["WinStreak"] = null,
                ["RankedTier"] = tier,
                ["Handicap"] = 0,
                ["RingoutVfx"] = playerCosmetics["RingoutVfx"]?.DeepClone() ?? "ring_out_vfx_default",
                ["Character"] = character,
                ["Banner"] = playerCosmetics["Banner"]?.DeepClone() ?? "banner_default",
                ["StatTrackers"] = statTrackers,
                ["Perks"] = new JsonArray(),
                ["PlayerIndex"] = player["playerIndex"]?.DeepClone(),
                ["PartyId"] = player["partyId"]?.DeepClone(),
                ["Username"] = new JsonObject(),
                ["Buffs"] = buffs.DeepClone(),
                ["Skin"] = skin,
                ["BotDifficultyMin"] = 0,
            };
        }
        catch (Exception e) when (e is StatTrackerValues.UnreadableException or MongoException or TimeoutException)
        {
            log.LogError("Error creating the gameplay config of {Player} for match {Match} ({Error}): the minimal one", id, matchId, e.Message);
            return new JsonObject
            {
                ["Taunts"] = new JsonArray("", "", "", ""),
                ["BotBehaviorOverride"] = "",
                ["AccountId"] = id,
                ["bAutoPartyPreference"] = false,
                ["Gems"] = new JsonArray(),
                ["PartyMember"] = null,
                ["GameplayPreferences"] = preferences,
                ["BotDifficultyMax"] = 0,
                ["bIsBot"] = false,
                ["RankedDivision"] = null,
                ["bUseCharacterDisplayName"] = false,
                ["StartingDamage"] = 0,
                ["TeamIndex"] = teamIndex?.DeepClone(),
                ["ProfileIcon"] = "profile_icon_default_gold",
                ["WinStreak"] = null,
                ["RankedTier"] = null,
                ["Handicap"] = 0,
                ["RingoutVfx"] = "ring_out_vfx_default",
                ["Character"] = character,
                ["Banner"] = "banner_default",
                ["StatTrackers"] = Trackers(1),
                ["Perks"] = new JsonArray(),
                ["PlayerIndex"] = player["playerIndex"]?.DeepClone(),
                ["PartyId"] = player["partyId"]?.DeepClone(),
                ["Username"] = new JsonObject(),
                ["Buffs"] = buffs.DeepClone(),
                ["Skin"] = skin,
                ["BotDifficultyMin"] = 0,
            };
        }
    }

    // RankedTier and RankedDivision for the fighter's rating in the match's mode (2v2's for 2v2, 1v1's otherwise), or the
    // mode's rating when the fighter has none; no rank when the rating cannot be read.
    private async Task<(JsonNode? Tier, JsonNode? Division)> RankAsync(IMongoDatabase mongo, string id, string character, string? gameMode, GameplayConfigMode mode, CancellationToken ct)
    {
        try
        {
            var rating = mode == GameplayConfigMode.On
                ? await ratings.GetOrCreateAsync(mongo.GetCollection<BsonDocument>("eloratings"), id, "", ct)
                : await mongo.GetCollection<BsonDocument>("eloratings").Find(new BsonDocument("account_id", id)).FirstOrDefaultAsync(ct)
                    ?? new BsonDocument { { "elo_1v1", ranked.CurrentValue.DefaultElo }, { "elo_2v2", ranked.CurrentValue.DefaultElo } };

            bool is2v2 = gameMode == "2v2";
            var charData = rating.GetValue(is2v2 ? "characters_2v2" : "characters_1v1", BsonNull.Value) is BsonDocument chars
                ? chars.GetValue(character, BsonNull.Value)
                : BsonNull.Value;
            double elo = Lean.Truthy(charData)
                ? (charData is BsonDocument data ? NumberOf(data.GetValue("elo", BsonUndefined.Value)) : double.NaN)
                : NumberOf(rating.GetValue(is2v2 ? "elo_2v2" : "elo_1v1", BsonUndefined.Value));
            var (tier, division) = RankedTiers.Of(elo);
            return (tier, division);
        }
        catch (Exception e) when (e is MongoException or TimeoutException)
        {
            log.LogWarning("Could not look up rank for {Player}: {Error}", id, e.Message);
            return (null, null);
        }
    }

    private static JsonArray Trackers(int value) => new(
        new JsonArray("stat_tracking_bundle_default", value),
        new JsonArray("stat_tracking_bundle_default", value),
        new JsonArray("stat_tracking_bundle_default", value));

    // Object.assign(target, source): a key already there is replaced where it is, a new one goes last.
    private static void Assign(JsonObject target, JsonObject source)
    {
        foreach (var (key, value) in source)
        {
            target[key] = value?.DeepClone();
        }
    }

    // value?.[key] for a JSON value: only an object has properties (TS read text and arrays by key too, which gives
    // nothing for these keys).
    private static JsonNode? Get(JsonNode? value, string key) => value is JsonObject o ? o[key] : null;

    // value[i]: an array's item, text's character, an object's property "i"; nothing otherwise.
    private static JsonNode? Index(JsonNode? value, int i) => value switch
    {
        JsonArray a => i < a.Count ? a[i] : null,
        JsonObject o => o[i.ToString(System.Globalization.CultureInfo.InvariantCulture)],
        JsonValue v when v.TryGetValue(out string? s) => i < s.Length ? s[i].ToString() : null,
        _ => null,
    };

    // Number(x) of a stored value, as JavaScript reads it.
    internal static double NumberOf(BsonValue value) => value switch
    {
        BsonInt32 i => i.Value,
        BsonInt64 l => l.Value,
        BsonDouble d => d.Value,
        BsonBoolean b => b.Value ? 1 : 0,
        BsonNull => 0,
        BsonString s => Js.Number(s.Value),
        _ => double.NaN,
    };

    // A JS number as a JSON value: whole numbers as integers; NaN (which JSON cannot hold) as null.
    private static JsonNode? JsNumber(double d) =>
        double.IsNaN(d) || double.IsInfinity(d) ? null : d == Math.Floor(d) && Math.Abs(d) <= 9007199254740991 ? JsonValue.Create((long)d) : JsonValue.Create(d);

    private static string? Str(JsonNode? value) => value is JsonValue v && v.TryGetValue(out string? s) ? s : null;
}

/// <summary>
/// The value a stat tracker shows on a player's card, as the TS websocket's resolveStatTrackerValue: what the slug's suffix
/// names (wins, ring-outs, total or highest damage dealt) for the fighter its bundle belongs to (AssociatedCharacter,
/// stat-tracker-characters.json, from the TS server's data/inventoryDefs.ts by tools/matches/stat_tracker_characters.mjs),
/// summed over the player's playerstats entries for that fighter in both modes (characters_1v1 and characters_2v2; the
/// highest damage is the highest of them), rounded. An entry is that fighter's when its key is character_{AssociatedCharacter}
/// (in any case, or the alias below), or the key without "character_" and underscores is the AssociatedCharacter.
/// </summary>
internal static class StatTrackerValues
{
    /// <summary>Where the TS code threw (and the player got the minimal config).</summary>
    public sealed class UnreadableException(string message) : Exception(message);

    private static readonly Lazy<Dictionary<string, string>> s_characters = new(() =>
    {
        using var stream = typeof(StatTrackerValues).Assembly.GetManifestResourceStream("OpenVersus.Server.Core.Matches.stat-tracker-characters.json")
            ?? throw new InvalidOperationException("stat-tracker-characters.json is not embedded");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
    });

    // AssociatedCharacter (a C-code) -> the stored fighter key's suffix, where they differ.
    private static readonly Dictionary<string, string> s_aliases = new()
    {
        ["C034"] = "BananaGuard",
        ["C015"] = "taz",
        ["C017"] = "creature",
        ["C001"] = "wonder_woman",
        ["C003"] = "superman",
        ["C016"] = "tom_and_jerry",
    };

    public static long Value(JsonNode? slugNode, BsonDocument? stats)
    {
        if (!RollbackCallbacks.Truthy(slugNode))
        {
            return 0;
        }

        // TS called endsWith on it.
        if (slugNode is not JsonValue v || !v.TryGetValue(out string? slug))
        {
            throw new UnreadableException($"a stat tracker that is not text: {slugNode!.ToJsonString()}");
        }

        if (slug == "stat_tracking_bundle_default")
        {
            return 0;
        }

        string field;
        if (slug.EndsWith("highestdamagedealt", StringComparison.Ordinal) || slug.EndsWith("_highest_damage_dealt", StringComparison.Ordinal))
        {
            field = "highestDamageDealt";
        }
        else if (slug.EndsWith("totaldamagedealt", StringComparison.Ordinal) || slug.EndsWith("_total_damage_dealt", StringComparison.Ordinal))
        {
            field = "totalDamageDealt";
        }
        else if (slug.EndsWith("ringouts", StringComparison.Ordinal))
        {
            field = "ringouts";
        }
        else if (slug.EndsWith("wins", StringComparison.Ordinal) || EndsWithWinsNumber(slug))
        {
            field = "wins";
        }
        else
        {
            return 0;
        }

        string assoc = s_characters.Value.GetValueOrDefault(slug) ?? "";
        var candidates = new List<string>();
        if (assoc.Length > 0)
        {
            candidates.Add($"character_{assoc}");
            candidates.Add($"character_{assoc.ToLowerInvariant()}");
            if (s_aliases.TryGetValue(assoc, out string? alias))
            {
                candidates.Add($"character_{alias}");
            }
        }

        double total = 0;
        foreach (string mode in new[] { "characters_1v1", "characters_2v2" })
        {
            if (stats?.GetValue(mode, BsonNull.Value) is not BsonDocument chars)
            {
                continue;
            }

            foreach (var entry in chars)
            {
                string stored = entry.Name;
                string name = (stored.StartsWith("character_", StringComparison.Ordinal) ? stored["character_".Length..] : stored)
                    .ToLowerInvariant().Replace("_", "", StringComparison.Ordinal);
                bool match = candidates.Any(c => stored == c || stored.ToLowerInvariant() == c.ToLowerInvariant()) || name == assoc.ToLowerInvariant();
                if (!match)
                {
                    continue;
                }

                // TS read the field off the entry: a null one threw.
                if (entry.Value is BsonNull or BsonUndefined)
                {
                    throw new UnreadableException($"{mode}.{stored} is null");
                }

                double value = entry.Value is BsonDocument data ? GameplayConfigs.NumberOf(data.GetValue(field, BsonUndefined.Value)) : double.NaN;
                value = double.IsNaN(value) ? 0 : value;
                total = field == "highestDamageDealt" ? Math.Max(total, value) : total + value;
            }
        }

        // Math.round.
        return (long)Math.Floor(total + 0.5);
    }

    // /wins\d+$/
    private static bool EndsWithWinsNumber(string slug)
    {
        int end = slug.Length;
        while (end > 0 && char.IsAsciiDigit(slug[end - 1]))
        {
            end--;
        }

        return end < slug.Length && slug[..end].EndsWith("wins", StringComparison.Ordinal);
    }
}

/// <summary>A rating's ranked tier and division, as the TS server's eloToTierDivision.</summary>
public static class RankedTiers
{
    private static readonly (string Name, double Min, double Max)[] s_tiers =
    [
        ("Bronze", 0, 499),
        ("Silver", 500, 999),
        ("Gold", 1000, 1499),
        ("Platinum", 1500, 1999),
        ("Diamond", 2000, 2499),
        ("Master", 2500, 2999),
        ("Grandmaster", 3000, double.PositiveInfinity),
    ];

    /// <summary>
    /// Each tier spans its ratings in five divisions of 100 (the fifth takes the rest). A rating in none (negative, not a
    /// number, or between two tiers' bounds, which only a fractional one can be) is Bronze 1.
    /// </summary>
    public static (string Tier, int Division) Of(double elo)
    {
        foreach (var (name, min, max) in s_tiers)
        {
            if (elo >= min && elo <= max)
            {
                return (name, (int)Math.Min(5, Math.Floor((elo - min) / 100) + 1));
            }
        }

        return ("Bronze", 1);
    }
}

/// <summary>
/// The bridge (docs/MIGRATION-BRIDGES.md, 9): builds each match's config from the channels the TS websocket hears,
/// match:notifications and perks:notifications, one message at a time (a lock never overtakes its own config), as
/// GameplayConfigs:Mode says.
/// </summary>
internal sealed class GameplayConfigBridge(IServiceProvider services, IGameplayConfigs configs, IOptionsMonitor<GameplayConfigSettings> settings,
    ILogger<GameplayConfigBridge> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Once the app has started: the cluster settings are loaded by a hosted service, and before that the mode read
        // would be the configuration's, not a cluster override's.
        services.GetService<IHostApplicationLifetime>()?.ApplicationStarted.Register(() => log.LogWarning(
            "MIGRATION BRIDGE: gameplay configs are built from match:notifications and perks:notifications (GameplayConfigs:Mode, now {Mode}) while the TS websocket still sends them; see dotnet/docs/MIGRATION-BRIDGES.md (9)",
            settings.CurrentValue.Mode));
        if (services.GetService<IConnectionMultiplexer>() is not { } mux)
        {
            log.LogWarning("Gameplay configs are not built here: this service has no Redis (REDIS)");
            return;
        }

        var queue = Channel.CreateUnbounded<(string Channel, string Message)>(new UnboundedChannelOptions { SingleReader = true });
        var subscriber = mux.GetSubscriber();
        foreach (string channel in new[] { MatchLauncher.NotificationChannel, PerksLock.Channel })
        {
            await subscriber.SubscribeAsync(RedisChannel.Literal(channel), (_, message) => queue.Writer.TryWrite((channel, message.ToString())));
        }

        try
        {
            await foreach (var (channel, message) in queue.Reader.ReadAllAsync(stoppingToken))
            {
                var mode = settings.CurrentValue.Mode;
                if (mode == GameplayConfigMode.Off)
                {
                    continue;
                }

                try
                {
                    if (Js.Parse(message) is not JsonObject notification)
                    {
                        log.LogError("{Channel}: not a JSON object", channel);
                    }
                    else if (channel == MatchLauncher.NotificationChannel)
                    {
                        await configs.BuildAsync(notification, mode, stoppingToken);
                    }
                    else
                    {
                        await configs.PerksLockedAsync(notification, stoppingToken);
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    log.LogError(e, "{Channel}: the gameplay config failed", channel);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            await subscriber.UnsubscribeAsync(RedisChannel.Literal(MatchLauncher.NotificationChannel));
            await subscriber.UnsubscribeAsync(RedisChannel.Literal(PerksLock.Channel));
        }
    }
}

public static class GameplayConfigsHosting
{
    /// <summary>The match configs (GameplayConfigs:Mode), and the bridge that builds them from the TS channels.</summary>
    public static WebApplicationBuilder AddGameplayConfigs(this WebApplicationBuilder builder)
    {
        builder.AddSetting<GameplayConfigSettings>("GameplayConfigs");
        builder.AddEloRatings();
        if (!builder.Services.Any(d => d.ServiceType == typeof(ICosmeticsService)))
        {
            builder.AddCosmetics();
        }

        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.TryAddSingleton<IGameplayConfigs, GameplayConfigs>();
        builder.Services.AddHostedService<GameplayConfigBridge>();
        return builder;
    }
}

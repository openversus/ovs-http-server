using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Access;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Profiles;

// The profile lookups the game makes for the players it shows (friends, lobby, match): GET /accounts/wb_network/bulk
// and GET /profiles/bulk, both sent as PUT with x-hydra-http-method: GET. Ported from the TS server's
// modules/friends/friends.service.ts (getUserFriendDetails, getProfileBulk) and services/profileIcons.ts, branch
// infinity-war; tools/friends/friends_diff.mjs compares the two servers (replay: every account in a prod snapshot).
//
// Mongo, read     playertesters (name, hydraUsername, profile_icon) by _id; dataassets (enabled ProfileIconData)
// Redis, read     online_players (set of player ids)
// Nothing written.
//
// Differences from the TS server, none in what the game gets: the icons are read per request, not from the TS
// server's startup cache (which misses icons added since it started, until a data sync); presence is one SMISMEMBER,
// not one SISMEMBER per player; an id that is a number (never sent) is not looked up.

public interface IProfilesService
{
    /// <summary>GET /accounts/wb_network/bulk: <c>{ids: [...]}</c> (or the bare array) in, account details out.</summary>
    Task<JsonArray> WbNetworkAccountsAsync(JsonNode? body, CancellationToken ct = default);

    /// <summary>
    /// GET /profiles/bulk: <c>{ids: [...]}</c> in, profiles out. <paramref name="hydra"/>: the answer goes out as Hydra,
    /// where the TS server's <c>new Date()</c> becomes an empty map (JSON gets the time).
    /// </summary>
    Task<JsonArray> ProfilesAsync(JsonNode? body, bool hydra, CancellationToken ct = default);
}

internal sealed class ProfilesService(IServiceProvider services, TimeProvider time, ILogger<ProfilesService> log) : IProfilesService
{
    public const string DefaultProfileIcon = "profile_icon_default";
    private const string IdentityAvatar = "https://s3.amazonaws.com/wb-agora-hydra-ugc-dokken/identicons/identicon.584.png";

    /// <summary>
    /// The virtual sender of the client-update toast (TS services/updateNotificationProfiles.ts): the game looks its
    /// profile up like a player's. Account id, username.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> UpdateNotificationProfiles = new Dictionary<string, string>
    {
        ["00000000000000000000a003"] = "Update: github.com/openversus/ovs-client/releases",
    };

    // TS data/testCharacters.ts: kept out of the catalog (ENABLE_TEST_CHARACTERS, off on every server, is not ported).
    private static readonly HashSet<string> s_testCharacters = new(
        ["character_supershaggy", "character_Meeseeks", "character_C022", "character_C033", "character_cmanny", "character_manny", "character_C037", "character_C099"],
        StringComparer.OrdinalIgnoreCase);

    public async Task<JsonArray> WbNetworkAccountsAsync(JsonNode? body, CancellationToken ct)
    {
        // req.body.ids || req.body: a missing, null, false, 0 or "" ids falls back to the body (the game's own shape is
        // {ids}; a bare array is taken as the ids).
        var ids = Truthy(Ids(body)) ? Ids(body) : body;
        if (ids is not JsonArray list)
        {
            return [];
        }

        return await Guarded("GET /accounts/wb_network/bulk", async (mongo, redis) =>
        {
            var players = await FindPlayersAsync(mongo, list, ct);
            var online = await OnlineAsync(redis, players);
            var icons = await ProfileIconsAsync(mongo, ct);
            long now = time.GetUtcNow().ToUnixTimeSeconds();
            var results = new JsonArray();
            for (int i = 0; i < players.Count; i++)
            {
                var p = players[i];
                string id = p["_id"].AsObjectId.ToString(), username = Username(p);
                results.Add(new JsonObject
                {
                    ["updated_at"] = new JsonObject { ["_hydra_unix_date"] = now },
                    ["created_at"] = new JsonObject { ["_hydra_unix_date"] = now },
                    ["deleted"] = false,
                    ["orphaned"] = false,
                    ["orphaned_reason"] = null,
                    ["public_id"] = id,
                    ["identity.avatar"] = IdentityAvatar,
                    ["identity.default_username"] = true,
                    ["identity.alternate.wb_network"] = new JsonArray(new JsonObject { ["id"] = id, ["username"] = username, ["avatar"] = null }),
                    ["identity.alternate.steam"] = new JsonArray(new JsonObject { ["id"] = id, ["username"] = username, ["avatar"] = null }),
                    ["wb_account.completed"] = true,
                    ["wb_account.email_verified"] = true,
                    ["points"] = 0,
                    ["state"] = "normal",
                    ["wbplay_data_synced"] = false,
                    ["wbplay_identity"] = null,
                    ["locale"] = "en-US",
                    ["data.LastLoginPlatform"] = "EPlatform::PC",
                    ["server_data.ProfileIcon.Slug"] = IconSlug(p),
                    ["server_data.ProfileIcon.AssetPath"] = icons(p.GetValue("profile_icon", BsonNull.Value)),
                    ["server_data.CurrentXP"] = 100,
                    ["server_data.Level"] = 5,
                    ["id"] = id,
                    ["identity.username"] = username,
                    ["connections"] = new JsonArray(),
                    ["presence_state"] = online[i] ? 0 : 1,
                    ["presence"] = online[i] ? "online" : "offline",
                });
            }

            return results;
        }, ct);
    }

    public async Task<JsonArray> ProfilesAsync(JsonNode? body, bool hydra, CancellationToken ct)
    {
        // req.body.ids || []; anything but an array ends in the TS catch, which answers [].
        var ids = Truthy(Ids(body)) ? Ids(body) : new JsonArray();
        if (ids is not JsonArray list)
        {
            return [];
        }

        return await Guarded("GET /profiles/bulk", async (mongo, _) =>
        {
            var lookups = new JsonArray(list.Where(id => !(id is JsonValue v && v.TryGetValue<string>(out var s) && UpdateNotificationProfiles.ContainsKey(s))).Select(id => id?.DeepClone()).ToArray());
            var players = await FindPlayersAsync(mongo, lookups, ct);
            var icons = players.Count > 0 ? await ProfileIconsAsync(mongo, ct) : _ => "";
            var results = new JsonArray();
            foreach (var p in players)
            {
                results.Add(Profile(p["_id"].AsObjectId.ToString(), Username(p), IconSlug(p), icons(p.GetValue("profile_icon", BsonNull.Value)), hydra));
            }

            // The virtual profiles follow, in the order asked (a repeated id repeats).
            foreach (var id in list)
            {
                if (id is JsonValue v && v.TryGetValue<string>(out var s) && UpdateNotificationProfiles.TryGetValue(s, out var username))
                {
                    results.Add(Profile(s, username, DefaultProfileIcon, "", hydra));
                }
            }

            return results;
        }, ct);
    }

    private JsonObject Profile(string id, string username, string slug, string assetPath, bool hydra) => new()
    {
        ["id"] = id,
        ["account_id"] = id,
        ["updated_at"] = Now(hydra),
        ["created_at"] = Now(hydra),
        ["account"] = new JsonObject
        {
            ["deleted"] = false,
            ["orphaned"] = false,
            ["orphaned_reason"] = null,
            ["public_id"] = id,
            ["identity"] = new JsonObject { ["default_username"] = true, ["username"] = username },
            ["state"] = "normal",
            ["wbplay_data_synced"] = false,
            ["wbplay_identity"] = null,
            ["locale"] = "en-US",
            ["data"] = new JsonObject { ["LastLoginPlatform"] = "EPlatform::PC" },
            ["id"] = id,
            ["server_data"] = new JsonObject { ["ProfileIcon"] = new JsonObject { ["Slug"] = slug, ["AssetPath"] = assetPath } },
        },
    };

    // new Date(): mvs-dump encodes a Date as a map of its own keys (none); JSON.stringify writes toISOString().
    private JsonNode Now(bool hydra) => hydra
        ? new JsonObject()
        : JsonValue.Create(time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));

    private async Task<JsonArray> Guarded(string route, Func<IMongoDatabase, IDatabase, Task<JsonArray>> read, CancellationToken ct)
    {
        var mongo = services.GetService<IMongoDatabase>();
        var redis = services.GetService<IConnectionMultiplexer>()?.GetDatabase();
        if (mongo is null || redis is null)
        {
            log.LogError("{Route}: this service has no {Missing}", route, mongo is null ? "Mongo (MONGODB_URI)" : "Redis (REDIS)");
            return [];
        }

        try
        {
            return await read(mongo, redis);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log.LogError(e, "{Route} failed", route);
            return [];
        }
    }

    /// <summary>The players with these ids (ObjectId.isValid strings, any hex case), in the order Mongo returns them.</summary>
    private static async Task<List<BsonDocument>> FindPlayersAsync(IMongoDatabase mongo, JsonArray ids, CancellationToken ct)
    {
        var objectIds = new BsonArray();
        foreach (var id in ids)
        {
            if (id is JsonValue v && v.TryGetValue<string>(out var s) && s.Length == 24 && ObjectId.TryParse(s, out var oid))
            {
                objectIds.Add(oid);
            }
        }

        return objectIds.Count == 0 ? [] : await mongo.GetCollection<BsonDocument>(PlayerRecord.Collection)
            .Find(new BsonDocument("_id", new BsonDocument("$in", objectIds))).ToListAsync(ct);
    }

    private static async Task<bool[]> OnlineAsync(IDatabase redis, List<BsonDocument> players) =>
        players.Count == 0 ? [] : await redis.SetContainsAsync("online_players", players.Select(p => (RedisValue)p["_id"].AsObjectId.ToString()).ToArray());

    // p.name || p.hydraUsername || "Unknown"
    private static string Username(BsonDocument p) =>
        p.GetValue("name", BsonNull.Value) is { IsString: true } name && name.AsString.Length > 0 ? name.AsString
        : p.GetValue("hydraUsername", BsonNull.Value) is { IsString: true } hydra && hydra.AsString.Length > 0 ? hydra.AsString
        : "Unknown";

    // p.profile_icon || "profile_icon_default"
    private static string IconSlug(BsonDocument p) =>
        p.GetValue("profile_icon", BsonNull.Value) is { IsString: true } slug && slug.AsString.Length > 0 ? slug.AsString : DefaultProfileIcon;

    /// <summary>
    /// TS profileIconAssetPath: the first enabled icon with this slug's asset path, else the default icon's, else "".
    /// </summary>
    private static async Task<Func<BsonValue, string>> ProfileIconsAsync(IMongoDatabase mongo, CancellationToken ct)
    {
        var icons = (await mongo.GetCollection<BsonDocument>("dataassets")
                .Find(new BsonDocument { { "assetType", "ProfileIconData" }, { "enabled", true } })
                .Project(new BsonDocument { { "slug", 1 }, { "assetPath", 1 }, { "character_slug", 1 } })
                .ToListAsync(ct))
            .Where(i => !(i.GetValue("character_slug", BsonNull.Value) is { IsString: true } c && s_testCharacters.Contains(c.AsString)))
            .ToList();
        static string? PathOf(BsonDocument? icon) => icon?.GetValue("assetPath", BsonNull.Value) is { IsString: true } path ? path.AsString : null;
        string? fallback = PathOf(icons.FirstOrDefault(i => i.GetValue("slug", BsonNull.Value) == DefaultProfileIcon));
        return slug => PathOf(icons.FirstOrDefault(i => i.GetValue("slug", BsonNull.Value).Equals(slug))) ?? fallback ?? "";
    }

    // body.ids: undefined unless the body is an object (JsonNode's indexer throws on an array or a value).
    private static JsonNode? Ids(JsonNode? body) => body is JsonObject o ? o["ids"] : null;

    // JavaScript truthiness of a JSON value.
    private static bool Truthy(JsonNode? node) => node switch
    {
        null => false,
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        JsonValue v when v.TryGetValue<string>(out var s) => s.Length > 0,
        JsonValue v when v.TryGetValue<double>(out var d) => d != 0 && !double.IsNaN(d),
        _ => true,
    };
}

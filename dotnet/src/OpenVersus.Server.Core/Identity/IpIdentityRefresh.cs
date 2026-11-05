using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using OpenVersus.Server.Core.Access;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Identity;

/// <summary>
/// Keeps identity:{ip} alive while a registered OpenVersus client runs, from the identify token (<see cref="IdentifyTokens"/>), as the TS
/// server's refreshIpIdentityFromToken (services/identityService.ts) does on every /ovs/notifications poll. /api/identify
/// writes the record at launch for 5 minutes; without it a reconnect or a server restart would count an up-to-date
/// player as an unregistered client. Written only when absent, and only while no other account is active at the IP
/// (another device behind it would read the record as its own identity).
/// </summary>
public static class IpIdentityRefresh
{
    private static readonly TimeSpan s_activeSession = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan s_recordLifetime = TimeSpan.FromSeconds(300);

    /// <summary>Writes identity:{ip} from the verified client token's claims when it is absent; whether it wrote it.</summary>
    public static async Task<bool> RefreshAsync(IDatabase redis, JsonObject? claims, string ip, DateTimeOffset now)
    {
        // identityRegistered !== "1" in TS: the string only (the identify token signs "1" or "").
        if (ip.Length == 0 || claims is null || claims["identityRegistered"] is not JsonValue registered || registered.GetValueKind() != JsonValueKind.String
            || registered.GetValue<string>() != "1" || Text(claims["clientVersion"]) is not { Length: > 0 } clientVersion)
        {
            return false;
        }

        string key = $"identity:{ip}";
        if (await redis.KeyExistsAsync(key))
        {
            return false;
        }

        string accountId = Text(claims["id"]) is { Length: > 0 } id ? id : await AccountIdFromIndexesAsync(redis, claims);
        var activeKey = (RedisKey)$"active_ip_accounts:{ip}";
        await redis.SortedSetRemoveRangeByScoreAsync(activeKey, 0, now.ToUnixTimeMilliseconds() - s_activeSession.TotalMilliseconds);
        if ((await redis.SortedSetRangeByRankAsync(activeKey)).Any(active => active.ToString() != accountId))
        {
            return false;
        }

        var hardware = IdentityRules.NormalizeHardware(Text(claims["hardwareId"]), Text(claims["hardwareIdVersion"]), Text(claims["hardwareIdQuality"]));
        // The Steam id only as a ticket proved it (the identify token says so); an identify never signs one otherwise.
        bool steamVerified = IdentifyTokens.SteamVerified(claims);
        // TS redisSaveIdentityIfAbsent: a pending hash renamed into place only if the record is still absent.
        string pending = $"{key}:pending:{ObjectId.GenerateNewId()}";
        await redis.HashSetAsync(pending,
        [
            new HashEntry("steamId", steamVerified ? IdentityRules.Normalize(IdentityKind.Steam, claims["steamId"]) : ""),
            new HashEntry("steamVerified", steamVerified ? "1" : ""),
            new HashEntry("epicId", IdentityRules.Normalize(IdentityKind.Epic, claims["epicId"])),
            new HashEntry("hardwareId", hardware.HardwareId),
            new HashEntry("hardwareIdVersion", hardware.HardwareIdVersion),
            new HashEntry("hardwareIdQuality", hardware.HardwareIdQuality),
            new HashEntry("installId", IdentityRules.Normalize(IdentityKind.Install, claims["installId"])),
            new HashEntry("clientVersion", clientVersion),
            new HashEntry("identityRegistered", "1"),
        ]);
        await redis.KeyExpireAsync(pending, s_recordLifetime);
        bool written = await redis.KeyRenameAsync(pending, key, When.NotExists);
        if (!written)
        {
            await redis.KeyDeleteAsync(pending);
        }

        return written;
    }

    // The account a token's Steam, Epic or install id is indexed to (identity:{kind}:{id}), or "".
    private static async Task<string> AccountIdFromIndexesAsync(IDatabase redis, JsonObject claims)
    {
        foreach (var (kind, name, field) in new[] { (IdentityKind.Steam, "steam", "steamId"), (IdentityKind.Epic, "epic", "epicId"), (IdentityKind.Install, "install", "installId") })
        {
            string id = IdentityRules.Normalize(kind, claims[field]);
            if (id.Length > 0 && await redis.StringGetAsync($"identity:{name}:{id}") is { HasValue: true } account)
            {
                return account.ToString();
            }
        }

        return "";
    }

    // A claim as JavaScript's template literal or comparison would read it: a string as is, a number as its digits.
    private static string Text(JsonNode? value) => value?.GetValueKind() switch
    {
        JsonValueKind.String => value.GetValue<string>(),
        JsonValueKind.Number => value.ToJsonString(),
        _ => "",
    };
}

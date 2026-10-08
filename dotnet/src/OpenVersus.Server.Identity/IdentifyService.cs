using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Access;
using OpenVersus.Server.Core.Clients;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Settings;
using OpenVersus.Server.Core.Identity;
using OpenVersus.Server.Identity.Steam;
using StackExchange.Redis;

namespace OpenVersus.Server.Identity;

// POST /api/identify, ported from the TS server's handler (server.ts, branch infinity-war): the OpenVersus client
// registers who is at this IP before the game logs in, and gets the token it sends on its own calls. One difference,
// deliberate: a Steam id counts only when the client's Steam session ticket proves it. The TS server took the id on the
// client's word (and signed it into the token), so anyone could be anyone on Steam. A claimed id without a valid ticket
// is logged and dropped; the install id, the hardware fingerprint and the IP identify such a client, as the fallback
// chain always did for a client without Steam. An Epic id is still taken as claimed (there is no ticket for it yet).
//
// Redis, read     identity:{ip} (the previous record from this IP, merged as mergeIpIdentity does), identity:steam|epic|
//                 install:{id} (which account the id is indexed to), connections:{id} (exists: the live session)
// Redis, written  identity:{ip} (5 minutes: the login handshake), connections:{id} clientVersion, identityRegistered,
//                 nodePort (a late registration unlocking a session /access already built)
// Mongo, written  playertesters steamTicket (the verified ticket's fields and SHA-256 on the account it resolved to;
//                 never the ticket itself). A first launch has no account yet: the fields wait in identity:{ip} and
//                 the game's login (AccessService) puts them on the account it creates or binds.

/// <summary>How a registration ended.</summary>
public abstract record IdentifyResult
{
    /// <summary>Registered: <c>{ok, token, accountId}</c>.</summary>
    public sealed record Ok(JsonObject Response) : IdentifyResult;

    /// <summary>HTTP 426: the client must update before it plays (<c>client_update_required</c>); the registration was still recorded.</summary>
    public sealed record UpdateRequired(JsonObject Response) : IdentifyResult;

    /// <summary>HTTP 400 with <c>{error}</c>.</summary>
    public sealed record BadRequest(string Error) : IdentifyResult;

    /// <summary>This service cannot register anyone (no Redis, or no identify secret).</summary>
    public sealed record Unavailable(string Reason) : IdentifyResult;
}

public interface IIdentifyService
{
    /// <summary>A registration from <paramref name="ip"/> with the request's JSON body (any shape: fields it lacks are empty).</summary>
    Task<IdentifyResult> RegisterAsync(string ip, JsonObject body, CancellationToken ct = default);
}

internal sealed class IdentifyService(
    IServiceProvider services,
    IOptionsMonitor<AccessSettings> access,
    IOptionsMonitor<ClientSettings> clients,
    ISteamTicketVerifier tickets,
    TimeProvider time,
    ILogger<IdentifyService> log) : IIdentifyService
{
    private static readonly TimeSpan s_recordLifetime = TimeSpan.FromSeconds(300);

    /// <summary>What identity:{ip} holds (TS IpIdentity), plus whether the Steam id was proved.</summary>
    /// <param name="SteamTicket">The verified ticket's fields as canonical extended JSON (see <see cref="IdentityRecord.TicketField"/>), or "".</param>
    private sealed record IpIdentity(string SteamId, bool SteamVerified, string SteamTicket, string EpicId, HardwareSignal Hardware, string InstallId, string ClientVersion)
    {
        public bool Any => SteamId.Length > 0 || EpicId.Length > 0 || InstallId.Length > 0;
    }

    public async Task<IdentifyResult> RegisterAsync(string ip, JsonObject body, CancellationToken ct = default)
    {
        if (ip.Length == 0)
        {
            return new IdentifyResult.BadRequest("Could not determine IP");
        }

        var redis = services.GetService<IConnectionMultiplexer>()?.GetDatabase();
        string? secret = access.CurrentValue.IdentifySecret;
        if (redis is null || string.IsNullOrEmpty(secret))
        {
            string missing = redis is null ? "Redis (REDIS)" : "an identify secret (Access:IdentifySecret)";
            log.LogError("Refusing a registration from {Ip}: this service has no {Missing}", ip, missing);
            return new IdentifyResult.Unavailable($"no {missing}");
        }

        var now = time.GetUtcNow();
        var incoming = Incoming(body, ip, now);
        int nodePort = Core.Matches.P2P.ParseNodePort(body["nodePort"]);
        var identity = Merge(await ReadAsync(redis, ip), incoming);
        bool registered = identity.Any;
        string key = $"identity:{ip}";
        await redis.HashSetAsync(key,
        [
            new HashEntry("steamId", identity.SteamId),
            new HashEntry("epicId", identity.EpicId),
            new HashEntry("hardwareId", identity.Hardware.HardwareId),
            new HashEntry("hardwareIdVersion", identity.Hardware.HardwareIdVersion),
            new HashEntry("hardwareIdQuality", identity.Hardware.HardwareIdQuality),
            new HashEntry("installId", identity.InstallId),
            new HashEntry("clientVersion", identity.ClientVersion),
            new HashEntry("identityRegistered", registered ? "1" : ""),
            new HashEntry("nodePort", nodePort.ToString(CultureInfo.InvariantCulture)),
            new HashEntry("steamVerified", identity.SteamVerified ? "1" : ""),
            new HashEntry(IdentityRecord.TicketField, identity.SteamTicket),
        ]);
        await redis.KeyExpireAsync(key, s_recordLifetime);
        log.LogInformation("Identity registered for IP {Ip} - steam:{Steam} epic:{Epic} install:{Install} hardware:{Hardware} version:{Version} identity:{Registered} node:{Node}",
            ip, identity.SteamId.Length > 0 ? identity.SteamId + " (verified)" : "-", Dash(identity.EpicId), identity.InstallId.Length > 0 ? "yes" : "no",
            identity.Hardware.HardwareId.Length > 0 ? $"v{identity.Hardware.HardwareIdVersion}/{identity.Hardware.HardwareIdQuality}" : "none",
            identity.ClientVersion.Length > 0 ? identity.ClientVersion : "legacy", registered ? "registered" : "missing", nodePort > 0 ? nodePort : "none");

        // The account these ids are indexed to (a first launch has none yet): the token's id claim, so the resolver
        // takes its fast path. By the Steam id only when a ticket proved it.
        string resolvedId = "";
        foreach (var (name, id) in new[] { ("steam", identity.SteamVerified ? identity.SteamId : ""), ("epic", identity.EpicId), ("install", identity.InstallId) })
        {
            if (id.Length > 0 && await redis.StringGetAsync($"identity:{name}:{id}") is { HasValue: true } account)
            {
                resolvedId = account.ToString();
                break;
            }
        }

        if (identity.SteamVerified && resolvedId.Length > 0)
        {
            await RecordTicketAsync(identity, resolvedId, ct);
        }

        // Full AccountToken-compatible shape, in the TS server's claim order, plus whether the Steam id was proved.
        var claims = new JsonObject
        {
            ["id"] = resolvedId,
            ["steamId"] = identity.SteamId,
            ["epicId"] = identity.EpicId,
            ["hardwareId"] = identity.Hardware.HardwareId,
            ["hardwareIdVersion"] = identity.Hardware.HardwareIdVersion,
            ["hardwareIdQuality"] = identity.Hardware.HardwareIdQuality,
            ["installId"] = identity.InstallId,
            ["clientVersion"] = identity.ClientVersion,
            ["identityRegistered"] = registered ? "1" : "",
            ["nodePort"] = nodePort.ToString(CultureInfo.InvariantCulture),
            ["current_ip"] = ip,
            ["profile_id"] = "",
            ["public_id"] = "",
            ["wb_network_id"] = resolvedId,
            ["username"] = "",
            ["hydraUsername"] = "",
            ["lobby_id"] = "",
            ["GameplayPreferences"] = 964,
            ["steamVerified"] = identity.SteamVerified ? "1" : "",
        };
        string token = IdentifyTokens.Sign(claims, secret, now);

        // If /access won the startup race, unlock that live session now; the node port is carried over too.
        if (resolvedId.Length > 0 && registered && await redis.KeyExistsAsync($"connections:{resolvedId}"))
        {
            var fields = new List<HashEntry> { new("clientVersion", identity.ClientVersion), new("identityRegistered", "1") };
            if (nodePort > 0)
            {
                fields.Add(new HashEntry("nodePort", nodePort.ToString(CultureInfo.InvariantCulture)));
            }

            await redis.HashSetAsync($"connections:{resolvedId}", [.. fields]);
        }

        var gate = clients.CurrentValue;
        if (gate.VersionCheck && (!registered || ClientVersions.UpdateRequired(identity.ClientVersion, gate.MinimumVersion)))
        {
            return new IdentifyResult.UpdateRequired(new JsonObject
            {
                ["ok"] = false,
                ["error"] = "client_update_required",
                ["minimumVersion"] = gate.MinimumVersion,
                ["identityRequired"] = !registered,
            });
        }

        return new IdentifyResult.Ok(new JsonObject { ["ok"] = true, ["token"] = token, ["accountId"] = resolvedId.Length > 0 ? resolvedId : null });
    }

    // The request's identifiers, normalized as the TS server normalized them; the Steam id from the ticket alone.
    private IpIdentity Incoming(JsonObject body, string ip, DateTimeOffset now)
    {
        string claimed = IdentityRules.Normalize(IdentityKind.Steam, body["steamId"]);
        string ticketText = body["steamTicket"]?.GetValueKind() == JsonValueKind.String ? body["steamTicket"]!.GetValue<string>() : "";
        string steamId = "", ticketJson = "";
        if (ticketText.Length > 0)
        {
            switch (tickets.Check(ticketText, access.CurrentValue.SteamAppId, now))
            {
                case SteamTicketCheck.Verified ok:
                    steamId = ok.Ticket.SteamId;
                    ticketJson = TicketFields(ok, ip, now).ToJson(new MongoDB.Bson.IO.JsonWriterSettings { OutputMode = MongoDB.Bson.IO.JsonOutputMode.CanonicalExtendedJson });
                    if (claimed.Length > 0 && claimed != steamId)
                    {
                        log.LogWarning("A client at {Ip} claimed Steam id {Claimed}, but its ticket is for {Steam}: the ticket decides", ip, claimed, steamId);
                    }

                    break;
                case SteamTicketCheck.Refused refused:
                    log.LogWarning("Refused the Steam ticket from {Ip} ({Reason}); the Steam id it claims ({Claimed}) is not used", ip, refused.Reason, Dash(claimed));
                    break;
            }
        }
        else if (claimed.Length > 0)
        {
            log.LogInformation("A client at {Ip} claims Steam id {Claimed} without a ticket: not used as an identity (its install id, hardware and IP decide)", ip, claimed);
        }

        string version = body["clientVersion"]?.GetValueKind() == JsonValueKind.String ? Js.Trim(body["clientVersion"]!.GetValue<string>()) : "";
        return new IpIdentity(
            steamId,
            steamId.Length > 0,
            ticketJson,
            IdentityRules.Normalize(IdentityKind.Epic, body["epicId"]),
            IdentityRules.NormalizeHardware(StringOnly(body["hardwareId"]), JsString(body["hardwareIdVersion"]), StringOnly(body["hardwareIdQuality"])),
            IdentityRules.Normalize(IdentityKind.Install, body["installId"]),
            version.Length > 32 ? version[..32] : version);
    }

    private static async Task<IpIdentity?> ReadAsync(IDatabase redis, string ip)
    {
        var fields = (await redis.HashGetAllAsync($"identity:{ip}")).ToDictionary(e => e.Name.ToString(), e => e.Value.ToString());
        if (fields.Count == 0)
        {
            return null;
        }

        string Field(string name) => fields.GetValueOrDefault(name, "");
        // A record from before tickets (no steamVerified field) never proved its Steam id: the id is dropped here, so a
        // later call from the same install cannot inherit it. A Steam id in a record is always a proved one.
        bool verified = Field("steamVerified") == "1";
        return new IpIdentity(verified ? Field("steamId") : "", verified, verified ? Field(IdentityRecord.TicketField) : "", Field("epicId"),
            new HardwareSignal(Field("hardwareId"), Field("hardwareIdVersion"), Field("hardwareIdQuality")), Field("installId"), Field("clientVersion"));
    }

    // TS mergeIpIdentity: a second call from the same install only adds to what the first stored; another install
    // replaces the record. The Steam id's proof travels with the id.
    private static IpIdentity Merge(IpIdentity? stored, IpIdentity incoming)
    {
        if (stored is null || incoming.InstallId.Length == 0 || stored.InstallId != incoming.InstallId)
        {
            return incoming;
        }

        var steam = incoming.SteamId.Length > 0 ? incoming : stored;
        return new IpIdentity(
            steam.SteamId,
            steam.SteamId.Length > 0 && steam.SteamVerified,
            steam.SteamId.Length > 0 ? steam.SteamTicket : "",
            incoming.EpicId.Length > 0 ? incoming.EpicId : stored.EpicId,
            incoming.Hardware.HardwareId.Length > 0 ? incoming.Hardware : stored.Hardware,
            incoming.InstallId,
            incoming.ClientVersion.Length > 0 ? incoming.ClientVersion : stored.ClientVersion);
    }

    /// <summary>
    /// The verified ticket as the account keeps it: every decoded field, its SHA-256, when it was received and from
    /// where; never the ticket itself (it replays until it expires), only its hash to recognize it again.
    /// </summary>
    public static BsonDocument TicketFields(SteamTicketCheck.Verified verified, string ip, DateTimeOffset now)
    {
        var ticket = verified.Ticket;
        return new BsonDocument
        {
            { "ticket_hash", verified.Hash },
            { "steam_id", ticket.SteamId },
            { "app_id", (long)ticket.AppId },
            { "version", (long)ticket.Version },
            { "flags", (long)ticket.OwnershipFlags },
            { "licenses", new BsonArray(ticket.Licenses.Select(l => (long)l)) },
            { "dlc", new BsonArray(ticket.Dlc.Select(d => new BsonDocument { { "app_id", (long)d.AppId }, { "licenses", new BsonArray(d.Licenses.Select(l => (long)l)) } })) },
            { "ownership_generated_at", ticket.OwnershipGenerated.UtcDateTime },
            { "ownership_expires_at", ticket.OwnershipExpires.UtcDateTime },
            { "ownership_external_ip", ticket.OwnershipExternalIp },
            { "ownership_internal_ip", ticket.OwnershipInternalIp },
            { "token_generated_at", ticket.Session is { } s ? s.TokenGenerated.UtcDateTime : BsonNull.Value },
            { "session_external_ip", ticket.Session?.ExternalIp ?? "" },
            { "connection_time", ticket.Session is { } st ? (long)st.ConnectionTime : BsonNull.Value },
            { "connection_count", ticket.Session is { } sc ? (long)sc.ConnectionCount : BsonNull.Value },
            { "received_at", now.UtcDateTime },
            { "request_ip", ip },
        };
    }

    // The verified ticket's fields onto the account the registration resolved to (replacing the last ticket's).
    private async Task RecordTicketAsync(IpIdentity identity, string accountId, CancellationToken ct)
    {
        if (services.GetService<IMongoDatabase>() is not { } mongo)
        {
            log.LogWarning("The verified Steam ticket of {Steam} was not recorded on account {Account}: this service has no Mongo (MONGODB_URI)", identity.SteamId, accountId);
            return;
        }

        if (!ObjectId.TryParse(accountId, out var id))
        {
            return;
        }

        try
        {
            await mongo.GetCollection<BsonDocument>(PlayerRecord.Collection).UpdateOneAsync(new BsonDocument("_id", id),
                new BsonDocument("$set", new BsonDocument(IdentityRecord.TicketField, BsonDocument.Parse(identity.SteamTicket))), cancellationToken: ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogError(e, "Could not record the Steam ticket of {Steam} on account {Account}", identity.SteamId, accountId);
        }
    }

    private static string Dash(string value) => value.Length > 0 ? value : "-";

    // A JSON value as the TS code read it: typeof v === "string" ? v : ... (a number for the version, as String(v)).
    private static string? StringOnly(JsonNode? value) => value?.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    private static string JsString(JsonNode? value) => value?.GetValueKind() switch
    {
        JsonValueKind.String => value.GetValue<string>(),
        JsonValueKind.Number => value.ToJsonString(),
        _ => "",
    };
}

public static class IdentifyHosting
{
    /// <summary>The identify service: Steam tickets, the client gate's settings (the 426 check) and the account resolver's time.</summary>
    public static WebApplicationBuilder AddIdentify(this WebApplicationBuilder builder)
    {
        builder.AddSetting<ClientSettings>("Clients");
        builder.AddSteamTickets();
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.TryAddSingleton<IIdentifyService, IdentifyService>();
        return builder;
    }
}

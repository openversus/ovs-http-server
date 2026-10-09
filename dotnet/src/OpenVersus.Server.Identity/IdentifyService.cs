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
using OpenVersus.Server.Core.Epic;
using OpenVersus.Server.Core.Settings;
using OpenVersus.Server.Core.Steam;
using OpenVersus.Server.Core.Identity;
using OpenVersus.Server.Identity.Epic;
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
    IOptionsMonitor<SteamSettings> steam,
    IOptionsMonitor<EpicSettings> epic,
    ISteamTicketVerifier tickets,
    IEpicIdTokenVerifier epicTokens,
    TimeProvider time,
    ILogger<IdentifyService> log) : IIdentifyService
{
    private static readonly TimeSpan s_recordLifetime = TimeSpan.FromSeconds(300);
    private static readonly TimeSpan s_verdictPoll = TimeSpan.FromMilliseconds(100);

    /// <summary>What identity:{ip} holds (TS IpIdentity), plus whether the Steam id was proved.</summary>
    /// <param name="SteamTicket">The verified ticket's fields as canonical extended JSON (see <see cref="IdentityRecord.TicketField"/>), or "".</param>
    private sealed record IpIdentity(string SteamId, bool SteamVerified, string SteamTicket, string EpicId, HardwareSignal Hardware, string InstallId, string ClientVersion)
    {
        public bool Any => SteamId.Length > 0 || EpicId.Length > 0 || InstallId.Length > 0;

        /// <summary>Steam itself confirmed the ticket is live for this registration (an auth session is held: the game runs under that account).</summary>
        public bool SteamOnline { get; init; }

        /// <summary>The verified ticket's session part and SHA-256 (what the Steam identity service is asked with); empty once the record is read back.</summary>
        public ReadOnlyMemory<byte> AuthPart { get; init; }

        public string TicketHash { get; init; } = "";

        /// <summary>Steam refused this registration's ticket: a stored proof for the same install is dropped too (Merge).</summary>
        public bool SteamRefused { get; init; }

        /// <summary>The Epic id is the subject of a verified Epic ID token (with Epic verification enforced, the only way it counts).</summary>
        public bool EpicVerified { get; init; }

        /// <summary>The Epic ID token was refused: a stored proof for the same install is dropped too (Merge).</summary>
        public bool EpicRefused { get; init; }
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
        bool epicEnforced = epic.CurrentValue.Enforced;
        var incoming = await CheckEpicAsync(await AskSteamAsync(redis, Incoming(body, ip, now), ip, now, ct), body, ip, now, ct);
        int nodePort = Core.Matches.P2P.ParseNodePort(body["nodePort"]);
        var identity = Merge(await ReadAsync(redis, ip, epicEnforced), incoming);
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
            new HashEntry("epicVerified", identity.EpicVerified ? "1" : ""),
        ]);
        await redis.KeyExpireAsync(key, s_recordLifetime);
        log.LogInformation("Identity registered for IP {Ip} - steam:{Steam} epic:{Epic} install:{Install} hardware:{Hardware} version:{Version} identity:{Registered} node:{Node}",
            ip, identity.SteamId.Length > 0 ? identity.SteamId + (identity.SteamOnline ? " (verified, live on Steam)" : " (verified)") : "-",
            identity.EpicId.Length > 0 ? identity.EpicId + (identity.EpicVerified ? " (verified)" : epicEnforced ? " (claim)" : "") : "-", identity.InstallId.Length > 0 ? "yes" : "no",
            identity.Hardware.HardwareId.Length > 0 ? $"v{identity.Hardware.HardwareIdVersion}/{identity.Hardware.HardwareIdQuality}" : "none",
            identity.ClientVersion.Length > 0 ? identity.ClientVersion : "legacy", registered ? "registered" : "missing", nodePort > 0 ? nodePort : "none");

        // The account these ids are indexed to (a first launch has none yet): the token's id claim, so the resolver
        // takes its fast path. By the Steam id only when a ticket proved it.
        string resolvedId = "";
        foreach (var (name, id) in new[] { ("steam", identity.SteamVerified ? identity.SteamId : ""), ("epic", identity.EpicVerified || !epicEnforced ? identity.EpicId : ""), ("install", identity.InstallId) })
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

        if (identity.EpicVerified && resolvedId.Length > 0)
        {
            await RecordEpicAsync(identity, resolvedId, ip, now, ct);
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
            ["steamOnline"] = identity.SteamOnline ? "1" : "",
            ["epicVerified"] = identity.EpicVerified ? "1" : "",
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
        string steamId = "", ticketJson = "", hash = "";
        ReadOnlyMemory<byte> authPart = default;
        if (ticketText.Length > 0)
        {
            switch (tickets.Check(ticketText, access.CurrentValue.SteamAppId, now))
            {
                case SteamTicketCheck.Verified ok:
                    steamId = ok.Ticket.SteamId;
                    authPart = ok.Ticket.AuthPart;
                    hash = ok.Hash;
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
            version.Length > 32 ? version[..32] : version)
        {
            AuthPart = authPart,
            TicketHash = hash,
        };
    }

    // Steam itself (Steam:Enabled): the ticket's session part is queued for an auth session (a durable list: a service
    // that is restarting, or not yet connected, takes it when it is), and the verdict waited for, up to
    // Steam:IdentifyWaitMs, when a Steam identity service is connected now. OK proves the ticket is live for this launch
    // (and is presence); refused drops the Steam id as a bad signature would; no verdict in time, or no service to ask,
    // leaves the offline verdict standing (a verdict landing later is acted on from the service's side: presence, or a
    // disconnect and the login holding the id as a claim).
    private async Task<IpIdentity> AskSteamAsync(IDatabase redis, IpIdentity incoming, string ip, DateTimeOffset now, CancellationToken ct)
    {
        var options = steam.CurrentValue;
        if (!incoming.SteamVerified || !options.Enabled || incoming.AuthPart.Length == 0)
        {
            return incoming;
        }

        // The account the Steam id is indexed to, when there is one (a first launch has none yet; the service looks again at the verdict).
        string playerId = (await redis.StringGetAsync($"identity:steam:{incoming.SteamId}")).ToString();
        await SteamSessions.QueueOpenAsync(redis, new SteamSessions.OpenRequest(incoming.SteamId, playerId, ip, Convert.ToHexStringLower(incoming.AuthPart.Span), incoming.TicketHash, now.ToUnixTimeMilliseconds()));
        if (!await SteamSessions.ConnectedAsync(redis))
        {
            log.LogInformation("The ticket of {Steam} from {Ip} is queued for the Steam identity service, which is not connected now: the offline check stands for this registration", incoming.SteamId, ip);
            return incoming;
        }

        var deadline = now + TimeSpan.FromMilliseconds(options.IdentifyWaitMs);
        SteamSessions.SessionView? session;
        while (true)
        {
            session = await SteamSessions.ReadAsync(redis, incoming.SteamId);
            if (session is not null && session.TicketHash != incoming.TicketHash)
            {
                session = null;
            }

            if (session is { State: not SteamSessions.Pending })
            {
                break;
            }

            var left = deadline - time.GetUtcNow();
            if (left <= TimeSpan.Zero)
            {
                break;
            }

            await Task.Delay(left < s_verdictPoll ? left : s_verdictPoll, time, ct);
        }

        switch (session?.State)
        {
            case SteamSessions.Ok:
                log.LogInformation("Steam confirmed the ticket of {Steam} from {Ip} after {Wait} ms", incoming.SteamId, ip, (time.GetUtcNow() - now).TotalMilliseconds);
                return incoming with { SteamOnline = true };
            case SteamSessions.Refused:
                log.LogWarning("Steam REFUSED the ticket of {Steam} from {Ip} ({Response}): the Steam id is not used as an identity", incoming.SteamId, ip, session.Response);
                return incoming with { SteamId = "", SteamVerified = false, SteamTicket = "", AuthPart = default, TicketHash = "", SteamRefused = true };
            case SteamSessions.Unavailable:
                log.LogInformation("Steam could not be asked about the ticket of {Steam} from {Ip} ({Response}): the offline check stands", incoming.SteamId, ip, session.Response);
                return incoming;
            default:
                log.LogInformation("No verdict from Steam on the ticket of {Steam} from {Ip} within {Wait} ms: the offline check stands", incoming.SteamId, ip, options.IdentifyWaitMs);
                return incoming;
        }
    }

    // The Epic id: with verification enforced (Epic:Enabled and a client id), only the subject of a verified Epic ID
    // token (epicToken) counts; a claimed epicId without one, or with a refused one, is logged and dropped, and a refused
    // token takes a stored proof for the same install with it (Merge). When the token cannot be judged (no keys from
    // Epic), the claim stays in the record unverified: the login ignores it, nothing is refused. Not enforced: as before,
    // the claim is taken as given and a token is ignored.
    private async Task<IpIdentity> CheckEpicAsync(IpIdentity incoming, JsonObject body, string ip, DateTimeOffset now, CancellationToken ct)
    {
        string token = StringOnly(body["epicToken"]) ?? "";
        if (!epic.CurrentValue.Enforced)
        {
            if (token.Length > 0)
            {
                log.LogDebug("A client at {Ip} sent an Epic ID token, which is not checked (Epic:ClientId is not set): its Epic id {Epic} is taken as claimed", ip, Dash(incoming.EpicId));
            }

            return incoming;
        }

        if (token.Length == 0)
        {
            if (incoming.EpicId.Length > 0)
            {
                log.LogInformation("A client at {Ip} claims Epic id {Epic} without the game's Epic ID token: not used as an identity (its install id, hardware and IP decide)", ip, incoming.EpicId);
            }

            return incoming with { EpicId = "" };
        }

        switch (await epicTokens.CheckAsync(token, now, ct))
        {
            case EpicTokenCheck.Verified ok:
                if (incoming.EpicId.Length > 0 && incoming.EpicId != ok.AccountId)
                {
                    log.LogWarning("A client at {Ip} claimed Epic id {Claimed}, but its Epic ID token is for {Epic}: the token decides", ip, incoming.EpicId, ok.AccountId);
                }

                return incoming with { EpicId = ok.AccountId, EpicVerified = true };
            case EpicTokenCheck.Refused refused:
                log.LogWarning("Refused the Epic ID token from {Ip} ({Reason}); the Epic id it claims ({Claimed}) is not used", ip, refused.Reason, Dash(incoming.EpicId));
                return incoming with { EpicId = "", EpicRefused = true };
            case EpicTokenCheck.Unavailable unavailable:
                log.LogInformation("The Epic ID token from {Ip} could not be judged ({Reason}): the Epic id {Epic} stays a claim", ip, unavailable.Reason, Dash(incoming.EpicId));
                return incoming;
            default:
                return incoming;
        }
    }

    private static async Task<IpIdentity?> ReadAsync(IDatabase redis, string ip, bool epicEnforced)
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
        // Likewise an Epic id, once verification is enforced: a record's id counts only with the token's proof.
        bool epicVerified = Field("epicVerified") == "1";
        return new IpIdentity(verified ? Field("steamId") : "", verified, verified ? Field(IdentityRecord.TicketField) : "", epicVerified || !epicEnforced ? Field("epicId") : "",
            new HardwareSignal(Field("hardwareId"), Field("hardwareIdVersion"), Field("hardwareIdQuality")), Field("installId"), Field("clientVersion"))
        {
            EpicVerified = epicVerified && Field("epicId").Length > 0,
        };
    }

    // TS mergeIpIdentity: a second call from the same install only adds to what the first stored; another install
    // replaces the record. The Steam id's proof travels with the id.
    private static IpIdentity Merge(IpIdentity? stored, IpIdentity incoming)
    {
        if (stored is null || incoming.InstallId.Length == 0 || stored.InstallId != incoming.InstallId)
        {
            return incoming;
        }

        // A refused ticket takes the stored proof with it: the record must not say verified when Steam just said no.
        var steam = incoming.SteamId.Length > 0 || incoming.SteamRefused ? incoming : stored;
        // The Epic id: a verified or refused token decides; else a stored proof stands (a claim from the launcher's files
        // under an Epic key outage must not outrank it: there is no offline verdict to carry it); else as before.
        var epicSide = incoming.EpicVerified || incoming.EpicRefused ? incoming : stored.EpicVerified ? stored : incoming.EpicId.Length > 0 ? incoming : stored;
        return new IpIdentity(
            steam.SteamId,
            steam.SteamId.Length > 0 && steam.SteamVerified,
            steam.SteamId.Length > 0 ? steam.SteamTicket : "",
            epicSide.EpicId,
            incoming.Hardware.HardwareId.Length > 0 ? incoming.Hardware : stored.Hardware,
            incoming.InstallId,
            incoming.ClientVersion.Length > 0 ? incoming.ClientVersion : stored.ClientVersion)
        {
            SteamOnline = steam.SteamId.Length > 0 && steam.SteamOnline,
            EpicVerified = epicSide.EpicId.Length > 0 && epicSide.EpicVerified,
        };
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

    // The verified Epic id onto the account the registration resolved to, by the login's rule (AccessService.BackfillAsync):
    // an Epic launch's token lands in the same second as the game's own login, which may read the record before this
    // registration wrote it; so the proof is put on the account here as the Steam ticket is. An empty or claimed id
    // gives way to the proved one; an id the account holds proved stays (another person's; the login keeps them apart).
    private async Task RecordEpicAsync(IpIdentity identity, string accountId, string ip, DateTimeOffset now, CancellationToken ct)
    {
        if (services.GetService<IMongoDatabase>() is not { } mongo || !ObjectId.TryParse(accountId, out var id))
        {
            return;
        }

        try
        {
            var players = mongo.GetCollection<BsonDocument>(PlayerRecord.Collection);
            var account = await players.Find(new BsonDocument("_id", id)).Project(new BsonDocument { { "epicId", 1 }, { IdentityRecord.EpicProvedField, 1 } }).FirstOrDefaultAsync(ct);
            if (account is null)
            {
                return;
            }

            string stored = account.TryGetValue("epicId", out var e) && e.IsString ? e.AsString : "";
            bool storedProved = account.TryGetValue(IdentityRecord.EpicProvedField, out var at) && at.IsValidDateTime;
            if (stored == identity.EpicId && storedProved)
            {
                return;
            }

            if (stored.Length > 0 && stored != identity.EpicId && stored != "Unknown" && storedProved)
            {
                log.LogWarning("Account {Account} holds proved Epic id {Stored}; the registration from {Ip} proved {Epic}: kept as is", accountId, stored, ip, identity.EpicId);
                return;
            }

            if (stored.Length > 0 && stored != identity.EpicId && stored != "Unknown")
            {
                log.LogInformation("Account {Account} held Epic id {Stored} as a claim; the registration from {Ip} proved {Epic}: replaced", accountId, stored, ip, identity.EpicId);
            }

            await players.UpdateOneAsync(new BsonDocument("_id", id),
                new BsonDocument("$set", new BsonDocument { { "epicId", identity.EpicId }, { IdentityRecord.EpicProvedField, now.UtcDateTime } }), cancellationToken: ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogError(e, "Could not record the verified Epic id of {Ip} on account {Account}", ip, accountId);
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
        builder.AddSetting<SteamSettings>("Steam");
        builder.AddSteamTickets();
        builder.AddEpicIdTokens();
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.TryAddSingleton<IIdentifyService, IdentifyService>();
        return builder;
    }
}

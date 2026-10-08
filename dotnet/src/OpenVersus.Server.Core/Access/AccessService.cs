using System.ComponentModel;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Bans;
using OpenVersus.Server.Core.CustomLobbies;
using OpenVersus.Server.Core.Identity;
using OpenVersus.Server.Core.Seasons;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Access;

// The game's login (POST /access), ported from the TS server's handlers/access.ts (branch infinity-war). Everything
// below is read by TS services that still run beside this one (websocket, matchmaking, the website), so it is written
// exactly as the TS server writes it; tools/access/access_diff.mjs compares the two key for key.
//
// Redis, read
//   identity:{ip}                hash from /api/identify: steamId, epicId, hardwareId, hardwareIdVersion,
//                                hardwareIdQuality, installId, clientVersion, identityRegistered ("1"), nodePort
//   active_ip_accounts:{ip}      zset of player ids by last-seen ms (sessions within 90 s count)
//   ssc_custom_lobby_player:{playerId}  the custom lobby the player is still in from an earlier session, if any
// Redis, written
//   connections:{playerId}       hash: the account token's fields (GameplayPreferences as text), hardwareIdVersion,
//                                hardwareIdQuality, installId, clientVersion, identityRegistered, nodePort (the P2P
//                                node's port, from the identify token's claim or identity:{ip}; "0" for none; never in
//                                the account token), jwt; party_key; login_custom_lobby (the custom lobby the player was
//                                still in at this login, "" for none: their first create_party_lobby takes them out of it,
//                                CustomLobbyService; the IP's copy does not get it)
//   connections:{ip}             the same hash, for old clients; 120 s TTL
//   active_ip_accounts:{ip}      entries older than 90 s dropped, this player added (score: now ms); 180 s TTL
//   player:{playerId}:blocked    JSON array of blocked player ids
//   identity:steam|epic|install:{id}  the player id; 30 days
//   admin:ip_changed_at:{ip}     now (ms), when an account arrives at or leaves an IP
//   fun_fact_pending:{playerId}  "1", 60 s;  daily_toast_bonus_pending:{playerId}  toasts granted, 300 s
//   party_key:{key lowercased}   JSON { playerId, lobbyId: "", username }; 1 hour
//   player_ranked_set:{playerId}, ranked_disconnect:{playerId}  deleted (a set left over from before a restart)
// Mongo
//   playertesters                the player (see PlayerRecord for how it is saved); token and account hold the
//                                account token
//   playercounters               the daily toast bonus (see DailyToastBonus)
//   eloratings, playerstats      read for the stat trackers: wins_1v1, wins_2v2; characters_1v1/2v2.{slug}.{wins,
//                                losses, ringouts, totalDamageDealt, highestDamageDealt}
//   dataassets                   the profile icon's assetPath (assetType ProfileIconData, enabled)
//
// Differences from the TS server, none visible in the stored data: the Steam ticket in the request is not read (the
// game sends an encrypted app ticket, which only WB could decrypt, so the TS check never matched one); the player is
// written once per login rather than in three saves; the daily bonus takes two counter updates, not three; the Redis
// writes go in one round trip. A failed insert refuses the login instead of carrying on with an unsaved player.

/// <summary>Where the game's realtime (websocket) connection goes: WB_DOMAIN and the TS server's port variables.</summary>
public sealed class RealtimeSettings
{
    [Description("The websocket server's host name, sent to the game at login (WB_DOMAIN).")]
    public string Domain { get; set; } = "localhost";

    [Description("The websocket server's port (WEBSOCKET_PORT), used unless Secure is set.")]
    public int Port { get; set; } = 3000;

    [Description("Non-zero: the game connects with wss:// to SecurePort (USE_SECURE_WEBSOCKET).")]
    public int Secure { get; set; }

    [Description("The websocket server's TLS port (SECURE_WEBSOCKET_PORT).")]
    public int SecurePort { get; set; } = 5000;

    [Description("The realtime cluster's name in the login response (configuration.realtime.default-cluster).")]
    public string Cluster { get; set; } = "ec2-us-east-1-dokken";

    [Description("The realtime server's name within the cluster in the login response.")]
    public string ServerName { get; set; } = "ovs-realtime";

    [Description("The realtime server's UDP address in the login response (unused by OpenVersus).")]
    public string Udp { get; set; } = "0.0.0.0:0";

    public string Url => Secure != 0 ? $"wss://{Domain}:{SecurePort}" : $"ws://{Domain}:{Port}";

    /// <summary>configuration.realtime as the login response carries it.</summary>
    public JsonObject Configuration() => new()
    {
        ["enabled"] = true,
        ["default-cluster"] = Cluster,
        ["servers"] = new JsonObject { [Cluster] = new JsonObject { [ServerName] = new JsonObject { ["ws"] = Url, ["udp"] = Udp } } },
    };
}

/// <summary>How a login ended.</summary>
public abstract record AccessResult
{
    /// <summary>Logged in: the response to send.</summary>
    public sealed record Ok(JsonObject Response, string PlayerId) : AccessResult;

    /// <summary>The IP is banned: the TS server answers 200 with an empty body.</summary>
    public sealed record Banned : AccessResult;

    /// <summary>This service cannot log anyone in (no Mongo, Redis or JWT secret).</summary>
    public sealed record Unavailable(string Reason) : AccessResult;
}

public interface IAccessService
{
    /// <summary>A login from <paramref name="ip"/>, with the x-hydra-access-token header when the client sent one.</summary>
    Task<AccessResult> LoginAsync(string ip, string? accessToken, CancellationToken ct = default);

    /// <summary>DELETE /access: the TS server only logs it.</summary>
    Task LogoutAsync(string ip, CancellationToken ct = default);
}

internal sealed partial class AccessService(
    IServiceProvider services,
    IOptionsMonitor<AccessSettings> access,
    IOptionsMonitor<RealtimeSettings> realtime,
    IOptionsMonitor<SeasonSettings> seasons,
    IBanService bans,
    INameRules names,
    IPersonBans personBans,
    TimeProvider time,
    ILogger<AccessService> log) : IAccessService
{
    private const int IdentityWaitAttempts = 15;
    private static readonly TimeSpan s_identityWait = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan s_activeSession = TimeSpan.FromSeconds(90);
    private static readonly JsonSerializerOptions s_json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private sealed class Identity
    {
        public string SteamId = "", EpicId = "", InstallId = "", ClientVersion = "";
        public HardwareSignal Hardware = HardwareSignal.None;
        public bool Registered;
        // The Steam id was proved by the client's session ticket (an identify token or record says so); the game's own
        // session token carries the account's stored id, which this login bound before.
        public bool SteamVerified;
        // The verified ticket's decoded fields (the identify service's TicketFields, extended JSON), to keep on the account; or "".
        public string SteamTicket = "";
        // The UDP port of the client's P2P node (Matches/P2P.cs); 0 when it reported none.
        public int NodePort;
        public string Source = "none";

        public bool Any => SteamId.Length > 0 || EpicId.Length > 0 || InstallId.Length > 0;
    }

    public async Task<AccessResult> LoginAsync(string ip, string? accessToken, CancellationToken ct = default)
    {
        var mongo = services.GetService<IMongoDatabase>();
        var redis = services.GetService<IConnectionMultiplexer>()?.GetDatabase();
        string? secret = access.CurrentValue.JwtSecret;
        if (mongo is null || redis is null || string.IsNullOrEmpty(secret))
        {
            string missing = string.Join(", ", new[] { mongo is null ? "Mongo (MONGODB_URI)" : null, redis is null ? "Redis (REDIS)" : null, string.IsNullOrEmpty(secret) ? "a JWT secret (JWT_SECRET)" : null }.OfType<string>());
            log.LogError("Refusing a login from {Ip}: this service has no {Missing}", ip, missing);
            return new AccessResult.Unavailable($"no {missing}");
        }

        if (await bans.IsBannedAsync(ip))
        {
            log.LogWarning("A connection attempt from banned IP Address {Ip} was denied.", ip);
            return new AccessResult.Banned();
        }

        var now = time.GetUtcNow();
        string randomName = NewName();
        var identity = await ResolveIdentityAsync(redis, ip, accessToken, secret, now, ct);
        // Before an account is found or made: a banned person gets no new account under another identifier.
        if (await bans.FindAsync(new BanIdentifiers(SteamId: identity.SteamId, EpicId: identity.EpicId, HardwareId: identity.Hardware.HardwareId, InstallId: identity.InstallId)) is { } identified)
        {
            log.LogWarning("A login from {Ip} was denied: its {Kind} {Value} is banned ({Source}).", ip, identified.Kind, identified.Value, identified.Source);
            return new AccessResult.Banned();
        }

        var players = mongo.GetCollection<BsonDocument>(PlayerRecord.Collection);
        var (player, isNew) = await FindOrCreatePlayerAsync(players, redis, ip, identity, randomName, now.UtcDateTime, ct);
        string id = player.IdHex;
        // The account's own identifiers, which this login may not have sent, and the player id.
        if (await bans.FindAsync(new BanIdentifiers(
                SteamId: IdentityRules.Normalize(IdentityKind.Steam, player.Str("steamId")), EpicId: IdentityRules.Normalize(IdentityKind.Epic, player.Str("epicId")),
                HardwareId: IdentityRules.NormalizeHardware(player.Str("hardwareId"), player.Str("hardwareIdVersion"), player.Str("hardwareIdQuality")).HardwareId,
                InstallId: IdentityRules.Normalize(IdentityKind.Install, player.Str("installId")), PlayerId: id)) is { } stored)
        {
            log.LogWarning("A login from {Ip} as player {Player} was denied: its {Kind} {Value} is banned ({Source}).", ip, id, stored.Kind, stored.Value, stored.Source);
            return new AccessResult.Banned();
        }

        if (identity.Any && ip.Length > 0)
        {
            // The IP rule: this identified login releases stale IP links of other accounts reachable by their own id.
            var released = await players.UpdateManyAsync(IdentityRules.StaleIpLinkFilter(ip, player.Id, now.UtcDateTime), new BsonDocument("$set", new BsonDocument("ip", "")), cancellationToken: ct);
            if (released.ModifiedCount > 0)
            {
                log.LogInformation("Released {Count} stale IP link(s) at {Ip} (inactive {Days}+ days, reachable by their own id).", released.ModifiedCount, ip, IdentityRules.StaleIpLinkDays);
                await BumpIpAccountsChangedAsync(redis, ip, now);
            }
        }

        if (string.IsNullOrEmpty(player.Str("hydraUsername")))
        {
            player.Set("hydraUsername", randomName);
        }

        // A name set before a term was added to a list: banned bans the person, force-change renames them to their own
        // random name (or a new one). Before the account token and the session are written, so both carry the new name.
        string name = player.Str("name") ?? "";
        switch (names.Check(name).Hit)
        {
            case { List: NameList.Banned } banned:
                await personBans.BanAsync(new BanRequest(id, "banned name", "login", MatchedList: banned.ListName, MatchedTerm: banned.Term, RequestIp: ip), ct);
                return new AccessResult.Banned();
            case { List: NameList.ForceChange } force:
                string own = player.Str("hydraUsername") ?? "";
                string renamed = RandomName().IsMatch(own) ? own : randomName;
                player.Set("name", renamed);
                log.LogWarning("Forced a name change for player {Player}: \"{Old}\" contains the force-change term \"{Term}\"; renamed to {New}.", id, name, force.Term, renamed);
                break;
        }

        // A ranked set left over from before a restart.
        if (await redis.StringGetAsync($"player_ranked_set:{id}") is { HasValue: true } staleSet && staleSet.ToString().Length > 0)
        {
            log.LogInformation("Cleaning up stale ranked set {Set} for player {Player} on login", staleSet.ToString(), id);
            await redis.KeyDeleteAsync([new RedisKey($"player_ranked_set:{id}"), new RedisKey($"ranked_disconnect:{id}")]);
        }

        // The verified Steam ticket's fields stay with the account the Steam id names (a first launch brings them here
        // through identity:{ip}; later ones put them on the account from /api/identify directly).
        if (identity.SteamVerified && identity.SteamTicket.Length > 0 && identity.SteamId == IdentityRules.Normalize(IdentityKind.Steam, player.Str("steamId")))
        {
            try
            {
                var ticket = BsonDocument.Parse(identity.SteamTicket);
                if (player.Get(IdentityRecord.TicketField) is not BsonDocument kept || kept.GetValue("ticket_hash", "") != ticket.GetValue("ticket_hash", ""))
                {
                    player.Set(IdentityRecord.TicketField, ticket);
                }
            }
            catch (FormatException e)
            {
                // Not what /api/identify writes: the login goes on without it.
                log.LogWarning(e, "The Steam ticket record for {Ip} is not readable; player {Player} logs in without it", ip, id);
            }
        }

        var account = AccountToken(player, ip);
        player.Set("token", ToBson(account));
        player.Set("account", ToBson(account));
        if (player.Get("blockedPlayers") is null or BsonNull)
        {
            player.Set("blockedPlayers", new BsonArray());
        }

        await player.SaveAsync(players, ct);
        if (isNew)
        {
            log.LogInformation("No existing player matched [steamId={Steam}, epicId={Epic}, hardwareId={Hardware}, installId={Install}, ip={Ip}]. Created new player with id {Player} and name {Name}.",
                Dash(identity.SteamId), Dash(identity.EpicId), Dash(identity.Hardware.HardwareId), identity.InstallId.Length > 0 ? "<present>" : "-", ip, id, randomName);
            await BumpIpAccountsChangedAsync(redis, ip, now);
        }

        string token = AccessTokens.Sign(account, secret, AccessTokens.Lifetime(access.CurrentValue.TokenTtl), now);
        var realtimeSettings = realtime.CurrentValue;
        string ws = realtimeSettings.Url;
        log.LogInformation("Player {Player} - {Name} connected; ws: {Ws}", id, player.Str("name"), ws);

        await WriteSessionAsync(redis, player, account, identity, token, ip, now);

        var (granted, count) = await DailyToastBonus.TryGrantAsync(mongo, id, now, ct);
        if (granted > 0)
        {
            log.LogInformation("Daily toast bonus: +{Granted} to {Player} ({Name}), new count: {Count}", granted, id, player.Str("name"), count);
            await redis.StringSetAsync($"daily_toast_bonus_pending:{id}", granted.ToString(CultureInfo.InvariantCulture), TimeSpan.FromSeconds(300));
        }

        var stats = StatTrackers.From(
            await mongo.GetCollection<BsonDocument>("eloratings").Find(new BsonDocument("account_id", id)).FirstOrDefaultAsync(ct),
            await mongo.GetCollection<BsonDocument>("playerstats").Find(new BsonDocument("account_id", id)).FirstOrDefaultAsync(ct));
        string icon = player.Str("profile_icon") ?? "";
        var iconAsset = await mongo.GetCollection<BsonDocument>("dataassets")
            .Find(new BsonDocument { { "assetType", "ProfileIconData" }, { "enabled", true }, { "slug", icon } }).FirstOrDefaultAsync(ct);

        var response = LoginResponse.Build(
            new LoginValues(token, realtimeSettings.Configuration(), id, (string)account["profile_id"]!, (string)account["public_id"]!, id, (string)account["username"]!,
                (string)account["hydraUsername"]!, icon, iconAsset?.GetValue("assetPath", BsonNull.Value) is { IsString: true } path ? path.AsString : null,
                access.CurrentValue.IdentityAvatarUrl, access.CurrentValue.SteamAvatarUrl),
            stats);
        // The current season's profile data when the TS literal has none (Season 6: docs/SEASONS.md).
        SeasonalData.AddTo(response, seasons.CurrentValue.Current);
        return new AccessResult.Ok(response, id);
    }

    public async Task LogoutAsync(string ip, CancellationToken ct = default)
    {
        log.LogInformation("Received request to delete access from {Ip}", ip);
        if (services.GetService<IMongoDatabase>() is not { } mongo)
        {
            return;
        }

        var player = await mongo.GetCollection<BsonDocument>(PlayerRecord.Collection).Find(new BsonDocument("ip", ip)).FirstOrDefaultAsync(ct);
        if (player is null)
        {
            log.LogInformation("No player found for IP: {Ip}", ip);
            return;
        }

        log.LogInformation("Player {Player} with name {Name} and IP {Ip} is disconnecting.", player["_id"].ToString(), player.GetValue("name", "").ToString(), ip);
    }

    // Identity, in this order: a valid token's claims (per client, so it cannot race another player at the same IP):
    // the game's own session token from its last login, else the identify token the OpenVersus client puts on the login
    // when the game has none yet (the Steam id in it only when a ticket proved it); else the IP record /api/identify
    // wrote, else that record if it lands within 3 s (the client registers on a background thread and can be a moment
    // behind the login).
    private async Task<Identity> ResolveIdentityAsync(IDatabase redis, string ip, string? accessToken, string secret, DateTimeOffset now, CancellationToken ct)
    {
        var identity = new Identity();
        if (!string.IsNullOrEmpty(accessToken))
        {
            try
            {
                var claims = AccessTokens.Verify(accessToken, secret, now);
                if (Truthy(claims["steamId"]) || Truthy(claims["epicId"]) || Truthy(claims["installId"]))
                {
                    Fill(identity, claims, steamId: IdentityRules.Normalize(IdentityKind.Steam, claims["steamId"]));
                    identity.Source = "jwt";
                }
            }
            catch (AccessTokenException)
            {
                // Not a game session token of ours, or expired: an identify token, else the IP record decides.
                if (IdentifyTokens.Verify(accessToken, access.CurrentValue.IdentifySecret, now) is { } identify)
                {
                    bool verified = IdentifyTokens.SteamVerified(identify);
                    string steamId = verified ? IdentityRules.Normalize(IdentityKind.Steam, identify["steamId"]) : "";
                    if (!verified && Truthy(identify["steamId"]))
                    {
                        log.LogInformation("The identify token from {Ip} names Steam id {Steam} without a ticket's proof: ignored", ip, JsString(identify["steamId"]));
                    }

                    if (steamId.Length > 0 || Truthy(identify["epicId"]) || Truthy(identify["installId"]))
                    {
                        Fill(identity, identify, steamId);
                        identity.SteamVerified = steamId.Length > 0;
                        identity.Source = "identify";
                    }
                }
            }
        }

        if (identity.Source == "none" && await ReadIpIdentityAsync(redis, ip, identity))
        {
            identity.Source = "redis";
        }

        for (int attempt = 0; identity.Source == "none" && attempt < IdentityWaitAttempts; attempt++)
        {
            await Task.Delay(s_identityWait, time, ct);
            if (await ReadIpIdentityAsync(redis, ip, identity))
            {
                identity.Source = "redis-wait";
                log.LogInformation("Late ASI identity arrived during /access startup grace period for {Ip}.", ip);
            }
        }

        if (identity.SteamVerified && identity.SteamTicket.Length == 0)
        {
            // The ticket's fields travel in identity:{ip} (an identify token carries only the proof).
            var record = (await redis.HashGetAllAsync($"identity:{ip}")).ToDictionary(e => e.Name.ToString(), e => e.Value.ToString());
            if (record.GetValueOrDefault("steamId", "") == identity.SteamId && record.GetValueOrDefault("steamVerified", "") == "1")
            {
                identity.SteamTicket = record.GetValueOrDefault(IdentityRecord.TicketField, "");
            }
        }

        if (identity.Source is "jwt" or "identify")
        {
            log.LogInformation("Identity from {Source} claims (preferred over Redis): steam={Steam} epic={Epic} hw={Hardware}", identity.Source == "jwt" ? "JWT" : "identify token", Dash(identity.SteamId), Dash(identity.EpicId), Short(identity.Hardware.HardwareId));
        }
        else if (identity.Source == "redis")
        {
            log.LogDebug("Identity from Redis (no JWT present): steam={Steam} epic={Epic} hw={Hardware}", Dash(identity.SteamId), Dash(identity.EpicId), Short(identity.Hardware.HardwareId));
        }

        if (!identity.Any)
        {
            log.LogWarning("/access from IP {Ip} has no canonical identity; only an unambiguous active-IP session may be reused.", ip);
        }

        return identity;
    }

    // A token's identity fields; the Steam id as the caller decided it (the game's token: the account's own; an identify
    // token: only a proved one).
    private static void Fill(Identity identity, JsonObject claims, string steamId)
    {
        identity.SteamId = steamId;
        identity.EpicId = IdentityRules.Normalize(IdentityKind.Epic, claims["epicId"]);
        identity.Hardware = IdentityRules.NormalizeHardware(StringOnly(claims["hardwareId"]), JsString(claims["hardwareIdVersion"]), StringOnly(claims["hardwareIdQuality"]));
        identity.InstallId = IdentityRules.Normalize(IdentityKind.Install, claims["installId"]);
        identity.ClientVersion = Truthy(claims["clientVersion"]) ? JsString(claims["clientVersion"]) : "";
        identity.Registered = StringOnly(claims["identityRegistered"]) == "1";
        identity.NodePort = Matches.P2P.ParseNodePort(claims["nodePort"]);
    }

    private async Task<bool> ReadIpIdentityAsync(IDatabase redis, string ip, Identity identity)
    {
        var fields = (await redis.HashGetAllAsync($"identity:{ip}")).ToDictionary(e => e.Name.ToString(), e => e.Value.ToString());
        string Field(string name) => fields.GetValueOrDefault(name, "");
        // A Steam id in the record counts only when the client's session ticket proved it (/api/identify, steamVerified);
        // a record from before tickets, or from a client whose ticket was refused, claims one and that is all it does.
        bool steamVerified = Field("steamVerified") == "1";
        if (!steamVerified && Field("steamId").Length > 0)
        {
            log.LogInformation("The identity record of {Ip} names Steam id {Steam} without a ticket's proof: ignored", ip, Field("steamId"));
        }

        string steamId = steamVerified ? IdentityRules.Normalize(IdentityKind.Steam, Field("steamId")) : "";
        if (!(steamId.Length > 0 || Field("epicId").Length > 0 || Field("installId").Length > 0))
        {
            return false;
        }

        identity.SteamId = steamId;
        identity.SteamVerified = steamId.Length > 0;
        identity.SteamTicket = steamId.Length > 0 ? Field(IdentityRecord.TicketField) : "";
        identity.EpicId = IdentityRules.Normalize(IdentityKind.Epic, Field("epicId"));
        identity.Hardware = IdentityRules.NormalizeHardware(Field("hardwareId"), Field("hardwareIdVersion"), Field("hardwareIdQuality"));
        identity.InstallId = IdentityRules.Normalize(IdentityKind.Install, Field("installId"));
        identity.ClientVersion = Field("clientVersion");
        identity.Registered = Field("identityRegistered") == "1";
        // The TS server reads Number(nodePort) || 0; /api/identify only ever writes a parsed port or "0".
        identity.NodePort = Matches.P2P.ParseNodePort(Field("nodePort"));
        return true;
    }

    // Which account this login is: by Steam, Epic or install id; an identity-less login by the IP's one active session
    // or its one defensible historical owner; an identified login with no account may adopt the IP's id-less one; an
    // identity-less one reuses the IP's provisional account. Otherwise a new account (provisional without an id).
    private async Task<(PlayerRecord Player, bool IsNew)> FindOrCreatePlayerAsync(
        IMongoCollection<BsonDocument> players, IDatabase redis, string ip, Identity identity, string randomName, DateTime now, CancellationToken ct)
    {
        Task<BsonDocument?> FindOne(BsonDocument filter) => players.Find(filter).FirstOrDefaultAsync(ct)!;
        Task<List<BsonDocument>> Newest(BsonDocument filter, int limit) =>
            players.Find(filter).Sort(new BsonDocument("lastSeenAt", -1)).Limit(limit).ToListAsync(ct);

        BsonDocument? found = null;
        if (identity.SteamId.Length > 0)
        {
            found = await FindOne(new BsonDocument("steamId", identity.SteamId));
            if (found is not null && await players.CountDocumentsAsync(new BsonDocument("steamId", identity.SteamId), new CountOptions { Limit = 2 }, ct) > 1)
            {
                // Accounts made from claimed ids, before tickets: the first one found logs in, as the TS server did.
                log.LogWarning("More than one account carries Steam id {Steam}; the first found, {Player}, logs in", identity.SteamId, found["_id"].ToString());
            }
        }

        if (found is null && identity.EpicId.Length > 0)
        {
            found = await FindOne(new BsonDocument("epicId", identity.EpicId));
        }

        if (found is null && identity.InstallId.Length > 0)
        {
            var owner = await FindOne(new BsonDocument("installId", identity.InstallId));
            bool steamConflict = identity.SteamId.Length > 0 && Str(owner, "steamId") is { Length: > 0 } s && s != identity.SteamId;
            bool epicConflict = identity.EpicId.Length > 0 && Str(owner, "epicId") is { Length: > 0 } e && e != identity.EpicId;
            if (!steamConflict && !epicConflict)
            {
                found = owner;
            }
        }

        if (found is null && !identity.Any && ip.Length > 0)
        {
            if (await UniqueActiveAccountAsync(redis, ip, time.GetUtcNow()) is { } activeId && ObjectId.TryParse(activeId, out var oid))
            {
                found = await FindOne(new BsonDocument("_id", oid));
                if (found is not null)
                {
                    log.LogInformation("Recovered identity-less retry from the sole active account {Player} at {Ip}.", activeId, ip);
                }
            }

            if (found is null)
            {
                // Canonical owners are asked for separately, so no number of old empty ghosts can hide the real
                // account behind the limit.
                var canonical = await Newest(new BsonDocument { { "ip", ip }, { "$or", IdentityRules.DurableIdClauses() } }, 2);
                var historical = canonical.Count > 0 ? canonical : await Newest(new BsonDocument { { "ip", ip }, { "provisional", new BsonDocument("$ne", true) } }, 2);
                found = IdentityRules.ChooseUnambiguousLegacyIpCandidate(historical);
                if (found is not null)
                {
                    log.LogWarning("Recovered identity-less legacy /access as the sole unambiguous historical account {Player} at {Ip}.", found["_id"].ToString(), ip);
                }
                else if (historical.Count > 0)
                {
                    log.LogWarning("Refused legacy IP fallback for {Ip}: historical claimants make the address ambiguous.", ip);
                }
            }
        }

        if (found is null && identity.Any && ip.Length > 0)
        {
            var candidates = await Newest(IdentityRules.IdLessAccountFilter(ip, provisional: false), 2);
            candidates.AddRange(await Newest(IdentityRules.IdLessAccountFilter(ip, provisional: true), 1));
            found = IdentityRules.ChooseAdoptionCandidate(candidates);
            if (found is not null)
            {
                log.LogInformation("Adopted id-less account {Player}{Provisional} at {Ip} for a newly identified client.", found["_id"].ToString(), found.GetValue("provisional", false) == true ? " (provisional)" : "", ip);
            }
        }

        if (found is null && !identity.Any && ip.Length > 0)
        {
            // An identity-less login never creates a normal account (every retry of an outdated client would leave a
            // ghost): it reuses this IP's provisional account, or gets a new one below.
            found = (await Newest(new BsonDocument { { "ip", ip }, { "provisional", true } }, 1)).FirstOrDefault();
            if (found is not null)
            {
                log.LogInformation("Reusing provisional account {Player} for an identity-less login at {Ip}.", found["_id"].ToString(), ip);
            }
        }

        if (found is null)
        {
            if (identity.InstallId.Length > 0 && (identity.SteamId.Length > 0 || identity.EpicId.Length > 0))
            {
                await players.UpdateManyAsync(new BsonDocument("installId", identity.InstallId), new BsonDocument("$set", new BsonDocument("installId", "")), cancellationToken: ct);
            }

            return (PlayerRecord.New(new Dictionary<string, BsonValue>
            {
                ["provisional"] = !identity.Any,
                ["ip"] = ip,
                ["name"] = randomName,
                ["hydraUsername"] = randomName,
                ["GameplayPreferences"] = 964,
                ["steamId"] = identity.SteamId,
                ["epicId"] = identity.EpicId,
                ["hardwareId"] = identity.Hardware.HardwareId,
                ["hardwareIdVersion"] = identity.Hardware.HardwareIdVersion,
                ["hardwareIdQuality"] = identity.Hardware.HardwareIdQuality,
                ["installId"] = identity.InstallId,
                ["lastSeenAt"] = now,
                ["ipSeenAt"] = now,
            }, now), true);
        }

        var player = PlayerRecord.Load(found, now);
        await BackfillAsync(players, redis, player, identity, ip, now, ct);
        return (player, false);
    }

    // An existing account takes this login's ids where it has none (or an old IP-derived placeholder), a strong
    // fingerprint, the install id (which no other account keeps), and the IP.
    private async Task BackfillAsync(IMongoCollection<BsonDocument> players, IDatabase redis, PlayerRecord player, Identity identity, string ip, DateTime now, CancellationToken ct)
    {
        static bool IsStale(string? value) => string.IsNullOrEmpty(value) || value == "Unknown" || value.StartsWith("ip_", StringComparison.Ordinal);

        if (identity.SteamId.Length > 0 && IsStale(player.Str("steamId")))
        {
            player.Set("steamId", identity.SteamId);
        }

        if (identity.EpicId.Length > 0 && IsStale(player.Str("epicId")))
        {
            player.Set("epicId", identity.EpicId);
        }

        var hw = identity.Hardware;
        if (hw.HardwareId.Length > 0 && hw.HardwareIdVersion == "2" && hw.HardwareIdQuality == "strong"
            && (player.Str("hardwareId") != hw.HardwareId || player.Str("hardwareIdVersion") != "2" || player.Str("hardwareIdQuality") != "strong"))
        {
            player.Set("hardwareId", hw.HardwareId);
            player.Set("hardwareIdVersion", hw.HardwareIdVersion);
            player.Set("hardwareIdQuality", hw.HardwareIdQuality);
        }

        if (identity.InstallId.Length > 0 && player.Str("installId") != identity.InstallId)
        {
            // The install binding follows the current platform account; a previous owner stays reachable by its
            // Steam or Epic id but must not keep a copy of this install id.
            await players.UpdateManyAsync(
                new BsonDocument { { "_id", new BsonDocument("$ne", player.Id) }, { "installId", identity.InstallId } },
                new BsonDocument("$set", new BsonDocument("installId", "")), cancellationToken: ct);
            player.Set("installId", identity.InstallId);
        }

        if (player.Bool("provisional") && identity.Any)
        {
            player.Set("provisional", false);
        }

        if (player.Str("ip") != ip)
        {
            // An account moving IP: both IPs' admin cookies are re-verified.
            string? previous = player.Str("ip");
            player.Set("ip", ip);
            var at = new DateTimeOffset(now, TimeSpan.Zero);
            if (!string.IsNullOrEmpty(previous))
            {
                await BumpIpAccountsChangedAsync(redis, previous, at);
            }

            await BumpIpAccountsChangedAsync(redis, ip, at);
        }

        player.Set("lastSeenAt", now);
        player.Set("ipSeenAt", now);
    }

    // The account token: the JWT's claims (their order is the TS server's), the player's token and account fields,
    // and the Redis connection hash.
    private static JsonObject AccountToken(PlayerRecord player, string ip)
    {
        var gameplay = player.Get("GameplayPreferences") is { IsNumeric: true } g ? g.ToDouble() : 964;
        return new JsonObject
        {
            ["id"] = player.IdHex,
            ["profile_id"] = player.Get("profile_id") is { IsObjectId: true } p ? p.AsObjectId.ToString() : player.Str("profile_id") ?? "",
            ["public_id"] = player.Str("public_id") ?? "",
            ["wb_network_id"] = player.IdHex,
            ["hydraUsername"] = player.Str("hydraUsername") ?? "",
            ["username"] = player.Str("name") ?? "",
            ["current_ip"] = ip,
            ["lobby_id"] = "",
            ["GameplayPreferences"] = gameplay == Math.Floor(gameplay) && Math.Abs(gameplay) < 1e15 ? JsonValue.Create((long)gameplay) : JsonValue.Create(gameplay),
            ["steamId"] = player.Str("steamId") ?? "",
            ["epicId"] = player.Str("epicId") ?? "",
            ["hardwareId"] = player.Str("hardwareId") ?? "",
        };
    }

    // The token as the TS server's driver stores it: strings, and the number as a 32-bit integer when it fits.
    private static BsonDocument ToBson(JsonObject token)
    {
        var doc = new BsonDocument();
        foreach (var (name, value) in token)
        {
            if (value!.GetValueKind() != JsonValueKind.Number)
            {
                doc[name] = value.GetValue<string>();
                continue;
            }

            double d = value.AsValue().TryGetValue(out long whole) ? whole : value.GetValue<double>();
            doc[name] = d == Math.Floor(d) && d is >= int.MinValue and <= int.MaxValue ? new BsonInt32((int)d) : new BsonDouble(d);
        }

        return doc;
    }

    private async Task WriteSessionAsync(IDatabase redis, PlayerRecord player, JsonObject account, Identity identity, string token, string ip, DateTimeOffset now)
    {
        string id = player.IdHex;
        var connection = account.Select(p => new HashEntry(p.Key, p.Value is { } v && v.GetValueKind() == JsonValueKind.Number ? v.ToJsonString() : (string?)p.Value ?? "")).ToList();
        connection.Add(new HashEntry("hardwareIdVersion", player.Str("hardwareIdVersion") ?? ""));
        connection.Add(new HashEntry("hardwareIdQuality", player.Str("hardwareIdQuality") ?? ""));
        connection.Add(new HashEntry("installId", player.Str("installId") ?? ""));
        connection.Add(new HashEntry("clientVersion", identity.ClientVersion));
        connection.Add(new HashEntry("identityRegistered", identity.Registered ? "1" : ""));
        connection.Add(new HashEntry("nodePort", identity.NodePort.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        connection.Add(new HashEntry("jwt", token));
        // The IP hash was just written for this player, so the TS server's owner check before mirroring the party key
        // always passes here.
        if (player.Str("party_key") is { Length: > 0 } partyKey)
        {
            connection.Add(new HashEntry("party_key", partyKey));
        }

        var blocked = new JsonArray([.. (player.Get("blockedPlayers") as BsonArray ?? []).Select(b => (JsonNode?)(b.IsString ? JsonValue.Create(b.AsString) : JsonNode.Parse(b.ToJson())))]);
        // A login starts at the title screen: a custom lobby the player is still in is an earlier session's.
        string leftOver = (string?)await redis.StringGetAsync(CustomLobbyService.PlayerKey(id)) ?? "";
        long ms = now.ToUnixTimeMilliseconds();
        var batch = redis.CreateBatch();
        var writes = new List<Task>
        {
            batch.StringSetAsync($"player:{id}:blocked", blocked.ToJsonString(s_json)),
            batch.HashSetAsync($"connections:{id}", [.. connection, new HashEntry(CustomLobbyService.LoginField, leftOver)]),
            batch.HashSetAsync($"connections:{ip}", [.. connection]),
            batch.KeyExpireAsync($"connections:{ip}", TimeSpan.FromSeconds(120)),
            batch.StringSetAsync($"fun_fact_pending:{id}", "1", TimeSpan.FromSeconds(60)),
        };

        // Identity indexes, so later lookups resolve by id rather than by IP. Hardware ids are never indexed.
        var ttl = TimeSpan.FromDays(30);
        foreach (var (kind, name, value) in new[] { (IdentityKind.Steam, "steam", "steamId"), (IdentityKind.Epic, "epic", "epicId"), (IdentityKind.Install, "install", "installId") })
        {
            if (IdentityRules.Normalize(kind, player.Str(value)) is { Length: > 0 } normalized)
            {
                writes.Add(batch.StringSetAsync($"identity:{name}:{normalized}", id, ttl));
            }
        }

        if (player.Str("party_key") is { Length: > 0 } key)
        {
            string data = new JsonObject { ["playerId"] = id, ["lobbyId"] = "", ["username"] = player.Str("name") ?? "" }.ToJsonString(s_json);
            writes.Add(batch.StringSetAsync($"party_key:{key.ToLowerInvariant()}", data, TimeSpan.FromHours(1)));
        }

        batch.Execute();
        await Task.WhenAll(writes);

        // This session, among the IP's active ones.
        var session = redis.CreateTransaction();
        string active = $"active_ip_accounts:{ip}";
        _ = session.SortedSetRemoveRangeByScoreAsync(active, 0, ms - s_activeSession.TotalMilliseconds);
        _ = session.SortedSetAddAsync(active, id, ms);
        _ = session.KeyExpireAsync(active, TimeSpan.FromSeconds(180));
        await session.ExecuteAsync();
    }

    private static async Task<string?> UniqueActiveAccountAsync(IDatabase redis, string ip, DateTimeOffset now)
    {
        string key = $"active_ip_accounts:{ip}";
        await redis.SortedSetRemoveRangeByScoreAsync(key, 0, now.ToUnixTimeMilliseconds() - s_activeSession.TotalMilliseconds);
        var ids = await redis.SortedSetRangeByRankAsync(key);
        if (ids.Length != 1)
        {
            return null;
        }

        var connectionId = await redis.HashGetAsync($"connections:{ids[0]}", "id");
        return connectionId.HasValue && connectionId.ToString().Length > 0 ? connectionId.ToString() : null;
    }

    // Re-verifies admin cookies bound to this IP (the website compares their iat with this).
    private async Task BumpIpAccountsChangedAsync(IDatabase redis, string ip, DateTimeOffset now)
    {
        if (ip.Length == 0)
        {
            return;
        }

        try
        {
            await redis.StringSetAsync($"admin:ip_changed_at:{ip}", now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
        }
        catch (RedisException e)
        {
            log.LogError(e, "Error bumping admin:ip_changed_at:{Ip}", ip);
        }
    }

    // A name NewName gave (or the TS NameGenerator did).
    [GeneratedRegex("^OpenVersus_[0-9]+$")]
    private static partial Regex RandomName();

    // NameGenerator.NewName: OpenVersus_ and a 13-digit number.
    private static string NewName() => $"OpenVersus_{Random.Shared.NextInt64(1_000_000_000_000, 9_999_999_999_999)}";

    private static string? Str(BsonDocument? doc, string field) => doc is not null && doc.TryGetValue(field, out var v) && v.IsString ? v.AsString : null;

    private static string Dash(string value) => value.Length > 0 ? value : "-";

    private static string Short(string hardwareId) => hardwareId.Length > 0 ? hardwareId[..8] + "..." : "-";

    // JavaScript truthiness of a claim.
    private static bool Truthy(JsonNode? node) => node?.GetValueKind() switch
    {
        JsonValueKind.String => node.GetValue<string>().Length > 0,
        JsonValueKind.Number => node.GetValue<double>() is var d && d != 0 && !double.IsNaN(d),
        JsonValueKind.True or JsonValueKind.Object or JsonValueKind.Array => true,
        _ => false,
    };

    private static string? StringOnly(JsonNode? node) => node?.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : null;

    // String(value ?? ""): what the TS rules do with a claim that should be text but is a number or a boolean.
    private static string JsString(JsonNode? node) => node?.GetValueKind() switch
    {
        JsonValueKind.String => node.GetValue<string>(),
        JsonValueKind.Number => node.GetValue<double>().ToString("R", CultureInfo.InvariantCulture),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        null or JsonValueKind.Null => "",
        _ => node.ToJsonString(),
    };
}

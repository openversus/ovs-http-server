using System.ComponentModel;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Identity;
using OpenVersus.Server.Core.Settings;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Clients;

// Which players must update their OpenVersus client before anything that leads into a match (queueing, starting a custom
// match or a rift node, accepting a rematch), ported from the TS server's services/clientUpdateGate.ts and services/clientVersion.ts (branch
// infinity-war). A player is let through when the check is off, or when their session was registered through
// /api/identify and their client is at least the minimum version (no minimum: any version).
// The same doors close to a player whose game runs paks that are neither the game's nor OVS_* (2026-10-16: the client's
// X-OVS-Other-Paks, kept at login as connections:{id} otherPaks; Clients:BlockOtherPaks): those change the simulation, so a
// match with them desyncs. Offline (the Lab, Local Play) never asks the server, so they keep working there. Such a player
// gets a banner naming the paks instead of the update toast.
//
// Redis, read     connections:{id} clientVersion, identityRegistered ("1"), otherPaks; client_update_modal_nonce:{id} (the calendar)
// Redis, written  client_update_modal_cooldown:{id} "1" (SET NX EX 15: one request per player per 15 s)
//                 client_update_modal_nonce:{id} (INCR, then EXPIRE 86400)
// Sent (ws:send)  the update toast (ToastReceivedNotification from the update notifier). The TS server published
//                 client_update:modal for its websocket, which sent the toast and closed the connection 10 s later; here
//                 the connection stays open: the player is turned away at every gameplay transition instead, each time
//                 with the toast (at most one per 15 s)

/// <summary>The client gate's settings (MIN_CLIENT_VERSION, CLIENT_VERSION_CHECK).</summary>
public sealed class ClientSettings
{
    [Description("On: gameplay needs a client registered through /api/identify, at least MinimumVersion (CLIENT_VERSION_CHECK).")]
    public bool VersionCheck { get; set; } = true;

    [Description("The oldest client version allowed into matches and lobbies, such as 2026.09.27.1; empty for none (MIN_CLIENT_VERSION).")]
    public string MinimumVersion { get; set; } = "";

    [Description("Where an outdated player is told to get the update.")]
    public string UpdateUrl { get; set; } = "https://github.com/openversus/ovs-client/releases";

    [Description("What an outdated player is told.")]
    public string UpdateMessage { get; set; } = "A required OpenVersus update is available. Download and install it before playing online.";

    [Description("The GitHub \"owner/repo\" whose latest release the client's update check offers (CLIENT_RELEASE_REPO); anything else means the default, openversus/ovs-client.")]
    public string ReleaseRepo { get; set; } = "openversus/ovs-client";

    [Description("The OVS paks players must run (RequiredPaks.cs): release = the latest ReleaseRepo release's OVS_* paks at GitHub's sha256 digests, the ones the client's startup update installs; off = none; or a literal Name=sha256;... list (local tests). A player whose paks (X-OVS-Paks, kept at login) differ is told to close the game and relaunch to update: when the list changes, unless in a match, and at the end of each match. Nobody is locked out for it.")]
    public string RequiredPaks { get; set; } = "release";

    [Description("What the game shows before it closes to install new paks.")]
    public string PaksUpdateMessage { get; set; } = "New OpenVersus content is out. MultiVersus will close in 5 seconds; please relaunch it to install the update.";

    [Description("On: a player whose game runs paks that are neither the game's nor OVS_* (X-OVS-Other-Paks) is kept out of online play; the Lab and Local Play stay theirs.")]
    public bool BlockOtherPaks { get; set; } = true;

    [Description("The banner such a player gets; {paks} becomes their names.")]
    public string OtherPaksMessage { get; set; } = "Online play is off while other paks are installed: {paks}. Remove them to play online; the Lab still works with them.";
}

/// <summary>A player's client, as the gate sees it.</summary>
/// <param name="Required">The client must update (version, identity).</param>
public sealed record ClientUpdateState(string AccountId, string ClientVersion, bool IdentityRegistered, bool Required)
{
    /// <summary>The paks the game runs that are neither the game's nor OVS_* (";"-separated), when the gate blocks them; else "".</summary>
    public string OtherPaks { get; init; } = "";

    /// <summary>Kept out of online play: an update required, or other paks.</summary>
    public bool Blocked => Required || OtherPaks.Length > 0;
}

public interface IClientUpdateGate
{
    /// <summary>The players among <paramref name="playerIds"/> who must update (each once; empty ids ignored).</summary>
    Task<IReadOnlyList<ClientUpdateState>> RequiringUpdateAsync(IEnumerable<string> playerIds);

    /// <summary>
    /// Asks the websocket to show each player the update toast, at most once per player per 15 s. For each player, true
    /// when a request went out.
    /// </summary>
    Task<IReadOnlyList<bool>> RequestModalsAsync(IEnumerable<string> playerIds);

    /// <summary>
    /// The client of the player a request is from (TS getRequestClientUpdateState): the player the request resolves to
    /// (<see cref="IAccountResolver"/>) and that connection's fields, each falling back to the session token's claim of
    /// the same name as JavaScript's <c>||</c> does (a stored "0" is kept, an empty one is not).
    /// </summary>
    Task<ClientUpdateState> ForRequestAsync(AccountLookup lookup, JsonObject? claims);

    /// <summary>
    /// How many update toasts the player has been sent (redisGetClientUpdateModalNonce): parseInt(value, 10), 0 for no
    /// player, no value or not a number.
    /// </summary>
    Task<double> ModalNonceAsync(string playerId);

    /// <summary>The Hydra action answer that turns a blocked player away: HTTP 200 with return_code 1, so the game does not retry.</summary>
    JsonObject FailureBody();
}

/// <summary>The TS server's version rules (clientVersion.ts).</summary>
public static partial class ClientVersions
{
    /// <summary>"v2026.09.27.1-beta" -> [2026, 9, 27, 1]: one to four dot-separated numbers; null for anything else.</summary>
    public static double[]? Parts(string version)
    {
        string clean = Js.Trim(version);
        if (clean.Length > 0 && clean[0] is 'v' or 'V')
        {
            clean = clean[1..];
        }

        clean = clean.Split('-')[0];
        return VersionPattern().IsMatch(clean) ? clean.Split('.').Select(p => double.Parse(p, System.Globalization.CultureInfo.InvariantCulture)).ToArray() : null;
    }

    // JavaScript's \d is ASCII only; so is [0-9].
    [GeneratedRegex(@"^[0-9]+(?:\.[0-9]+){0,3}$")]
    private static partial Regex VersionPattern();

    /// <summary>-1, 0 or 1; 0 when either is not a version. Missing parts count as 0.</summary>
    public static int Compare(string left, string right)
    {
        if (Parts(left) is not { } a || Parts(right) is not { } b)
        {
            return 0;
        }

        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            double delta = (i < a.Length ? a[i] : 0) - (i < b.Length ? b[i] : 0);
            if (delta != 0)
            {
                return delta < 0 ? -1 : 1;
            }
        }

        return 0;
    }

    /// <summary>
    /// Whether <paramref name="version"/> is below <paramref name="minimum"/>. No minimum (or one that is not a version):
    /// no. A minimum and a client that states no version: yes, it predates versions.
    /// </summary>
    public static bool UpdateRequired(string version, string minimum)
    {
        string configured = Js.Trim(minimum);
        if (configured.Length == 0 || Parts(configured) is null)
        {
            return false;
        }

        return Parts(version) is null || Compare(version, configured) < 0;
    }

    /// <summary>Whether gameplay is closed to this client: registration is required even when there is no minimum.</summary>
    public static bool GameplayAccessRequired(string version, string minimum, bool identityRegistered, bool checkEnabled) =>
        checkEnabled && (!identityRegistered || UpdateRequired(version, minimum));
}

internal sealed class ClientUpdateGate(IServiceProvider services, IAccountResolver resolver, IOptionsMonitor<ClientSettings> settings) : IClientUpdateGate
{
    private static readonly TimeSpan s_cooldown = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan s_nonceLifetime = TimeSpan.FromDays(1);

    /// <summary>The profile the update toast is from (the TS UPDATE_NOTIFICATION_PROFILES[0]; ProfilesService names it).</summary>
    public const string UpdateNotifierId = "00000000000000000000a003";

    private IDatabase Redis => services.GetService<IConnectionMultiplexer>()?.GetDatabase()
        ?? throw new InvalidOperationException("this service has no Redis (REDIS)");

    public async Task<IReadOnlyList<ClientUpdateState>> RequiringUpdateAsync(IEnumerable<string> playerIds)
    {
        var redis = Redis;
        var current = settings.CurrentValue;
        var states = await Task.WhenAll(playerIds.Where(id => id.Length > 0).Distinct().Select(async id =>
        {
            var fields = await redis.HashGetAsync($"connections:{id}", ["clientVersion", "identityRegistered", "otherPaks"]);
            string version = fields[0].HasValue ? fields[0].ToString() : "";
            bool registered = fields[1] == "1";
            return new ClientUpdateState(id, version, registered, ClientVersions.GameplayAccessRequired(version, current.MinimumVersion, registered, current.VersionCheck))
            {
                OtherPaks = current.BlockOtherPaks && fields[2].HasValue ? fields[2].ToString() : "",
            };
        }));
        return states.Where(s => s.Blocked).ToList();
    }

    public async Task<ClientUpdateState> ForRequestAsync(AccountLookup lookup, JsonObject? claims)
    {
        var connection = await resolver.ResolveAsync(lookup);
        string Field(string name) => connection?.Connection.FirstOrDefault(e => e.Name == name).Value is { HasValue: true } v ? v.ToString() : "";
        JsonNode? Claim(string name) => claims?[name];
        string ClaimText(string name) => Claim(name) is JsonValue v && v.TryGetValue(out string? text) ? text : "";

        string accountId = connection?.Id is { Length: > 0 } id ? id : ClaimText("id");
        string version = Field("clientVersion") is { Length: > 0 } stored ? stored : ClaimText("clientVersion");
        // hasRegisteredIdentity: "1" or true.
        bool registered = Field("identityRegistered") is { Length: > 0 } flag
            ? flag == "1"
            : Claim("identityRegistered") is JsonValue r && ((r.TryGetValue(out string? t) && t == "1") || (r.TryGetValue(out bool b) && b));
        var current = settings.CurrentValue;
        return new ClientUpdateState(accountId, version, registered, ClientVersions.GameplayAccessRequired(version, current.MinimumVersion, registered, current.VersionCheck))
        {
            OtherPaks = current.BlockOtherPaks ? Field("otherPaks") : "",
        };
    }

    public async Task<double> ModalNonceAsync(string playerId)
    {
        if (playerId.Length == 0)
        {
            return 0;
        }

        var value = await Redis.StringGetAsync($"client_update_modal_nonce:{playerId}");
        double nonce = Js.ParseInt(value.HasValue && value.ToString().Length > 0 ? value.ToString() : "0", 10);
        return double.IsNaN(nonce) ? 0 : nonce;
    }

    public async Task<IReadOnlyList<bool>> RequestModalsAsync(IEnumerable<string> playerIds)
    {
        var redis = Redis;
        return await Task.WhenAll(playerIds.Where(id => id.Length > 0).Distinct().Select(async id =>
        {
            if (!await redis.StringSetAsync($"client_update_modal_cooldown:{id}", "1", s_cooldown, When.NotExists))
            {
                return false;
            }

            // Other paks: the banner naming them (an update toast would send them looking for an update they have).
            var current = settings.CurrentValue;
            string other = current.BlockOtherPaks ? (string?)await redis.HashGetAsync($"connections:{id}", "otherPaks") ?? "" : "";
            if (other.Length > 0)
            {
                await Realtime.PlayerMessages.NotifyClientAsync(redis, id, "admin_banner", "Other paks installed",
                    current.OtherPaksMessage.Replace("{paks}", other.Replace(";", ", "), StringComparison.Ordinal), new JsonObject { ["timeout"] = 10 },
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                return true;
            }

            string nonceKey = $"client_update_modal_nonce:{id}";
            await redis.StringIncrementAsync(nonceKey);
            await redis.KeyExpireAsync(nonceKey, s_nonceLifetime);
            await UpdateToastAsync(redis, id);
            return true;
        }));
    }

    // The update toast the TS websocket sent for client_update:modal, to the player's game (a player not connected gets
    // nothing: no gateway holds them). Unlike the TS websocket, the connection is not closed
    // afterwards: an outdated player stays online and is turned away at each gameplay transition.
    private static Task UpdateToastAsync(IDatabase redis, string playerId) =>
        Realtime.PlayerMessages.SendAsync(redis, [playerId], Realtime.ProfileNotifications.Message(new JsonObject
        {
            ["template_id"] = "ToastReceivedNotification",
            ["ToasterAccountID"] = UpdateNotifierId,
            ["RewardsGranted"] = new JsonArray(),
        }, playerId));

    public JsonObject FailureBody()
    {
        var current = settings.CurrentValue;
        return new JsonObject
        {
            ["body"] = new JsonObject
            {
                ["error"] = "client_update_required",
                ["ErrorCode"] = "ClientOutdated",
                ["ErrorMessage"] = current.UpdateMessage,
                ["MinimumVersion"] = current.MinimumVersion,
                ["UpdateUrl"] = current.UpdateUrl,
            },
            ["metadata"] = null,
            ["return_code"] = 1,
        };
    }
}

public static class ClientUpdateGates
{
    /// <summary>
    /// The gate where a match is about to be made: when any of <paramref name="playerIds"/> must update, each of them is
    /// sent the update toast (at most one per 15 s) and the refusal is logged as "Blocked <paramref name="what"/>"; true
    /// when the match must not be made (the caller answers with <see cref="IClientUpdateGate.FailureBody"/> or its own
    /// refusal). The request routes are gated by path as well (ClientGameplayGate); this covers everyone the match would
    /// include, which the requester alone does not.
    /// </summary>
    public static async Task<bool> BlockOutdatedAsync(this IClientUpdateGate gate, IEnumerable<string> playerIds, ILogger log, string what)
    {
        var outdated = await gate.RequiringUpdateAsync(playerIds);
        if (outdated.Count == 0)
        {
            return false;
        }

        await gate.RequestModalsAsync(outdated.Select(o => o.AccountId));
        log.LogWarning("Blocked {What}: update required or other paks for {Players}", what,
            string.Join(", ", outdated.Select(o => $"{o.AccountId}:{(o.ClientVersion.Length > 0 ? o.ClientVersion : "legacy")}{(o.OtherPaks.Length > 0 ? " paks " + o.OtherPaks : "")}")));
        return true;
    }
}

public static class ClientUpdateGateHosting
{
    public static WebApplicationBuilder AddClientUpdateGate(this WebApplicationBuilder builder)
    {
        builder.AddSetting<ClientSettings>("Clients");
        // ForRequestAsync finds the player's connection through it.
        builder.AddAccountResolver();
        builder.Services.AddSingleton<IClientUpdateGate, ClientUpdateGate>();
        // The required paks, for telling players at a match's end (RequiredPaks.cs).
        builder.AddRequiredPaks();
        return builder;
    }
}

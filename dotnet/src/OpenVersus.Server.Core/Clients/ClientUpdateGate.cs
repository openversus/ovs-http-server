using System.ComponentModel;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Identity;
using OpenVersus.Server.Core.Settings;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Clients;

// Which players must update their OpenVersus client before a gameplay transition (joining a lobby, queueing, starting a
// custom match), ported from the TS server's services/clientUpdateGate.ts and services/clientVersion.ts (branch
// infinity-war). A player is let through when the check is off, or when their session was registered through
// /api/identify and their client is at least the minimum version (no minimum: any version).
//
// Redis, read     connections:{id} clientVersion, identityRegistered ("1"); client_update_modal_nonce:{id} (the calendar)
// Redis, written  client_update_modal_cooldown:{id} "1" (SET NX EX 15: one request per player per 15 s)
//                 client_update_modal_nonce:{id} (INCR, then EXPIRE 86400)
// Published       client_update:modal {"playerId", "nonce"} (the TS websocket shows the player the update toast)

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
}

/// <summary>A player's client, as the gate sees it.</summary>
public sealed record ClientUpdateState(string AccountId, string ClientVersion, bool IdentityRegistered, bool Required);

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

internal sealed class ClientUpdateGate(IServiceProvider services, IOptionsMonitor<ClientSettings> settings) : IClientUpdateGate
{
    private static readonly TimeSpan s_cooldown = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan s_nonceLifetime = TimeSpan.FromDays(1);
    public const string ModalChannel = "client_update:modal";

    private IDatabase Redis => services.GetService<IConnectionMultiplexer>()?.GetDatabase()
        ?? throw new InvalidOperationException("this service has no Redis (REDIS)");

    public async Task<IReadOnlyList<ClientUpdateState>> RequiringUpdateAsync(IEnumerable<string> playerIds)
    {
        var redis = Redis;
        var current = settings.CurrentValue;
        var states = await Task.WhenAll(playerIds.Where(id => id.Length > 0).Distinct().Select(async id =>
        {
            var fields = await redis.HashGetAsync($"connections:{id}", ["clientVersion", "identityRegistered"]);
            string version = fields[0].HasValue ? fields[0].ToString() : "";
            bool registered = fields[1] == "1";
            return new ClientUpdateState(id, version, registered, ClientVersions.GameplayAccessRequired(version, current.MinimumVersion, registered, current.VersionCheck));
        }));
        return states.Where(s => s.Required).ToList();
    }

    public async Task<ClientUpdateState> ForRequestAsync(AccountLookup lookup, JsonObject? claims)
    {
        var connection = await services.GetRequiredService<IAccountResolver>().ResolveAsync(lookup);
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
        return new ClientUpdateState(accountId, version, registered, ClientVersions.GameplayAccessRequired(version, current.MinimumVersion, registered, current.VersionCheck));
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

            string nonceKey = $"client_update_modal_nonce:{id}";
            long nonce = await redis.StringIncrementAsync(nonceKey);
            await redis.KeyExpireAsync(nonceKey, s_nonceLifetime);
            await redis.PublishAsync(RedisChannel.Literal(ModalChannel), Js.Stringify(new JsonObject { ["playerId"] = id, ["nonce"] = nonce }));
            return true;
        }));
    }

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

public static class ClientUpdateGateHosting
{
    public static WebApplicationBuilder AddClientUpdateGate(this WebApplicationBuilder builder)
    {
        builder.AddSetting<ClientSettings>("Clients");
        builder.Services.AddSingleton<IClientUpdateGate, ClientUpdateGate>();
        return builder;
    }
}

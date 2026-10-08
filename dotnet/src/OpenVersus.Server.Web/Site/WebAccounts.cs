using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Access;
using OpenVersus.Server.Core.Hosting;
using StackExchange.Redis;

namespace OpenVersus.Server.Web.Site;

// Which account a browser page acts on (the TS server's resolvePlayerForWeb, server.ts): a browser has no session token,
// only its IP. The accounts at the IP (playertesters with ip, not provisional; no IP, none):
//   none   no account: the page says to connect from the game first
//   one    that account, and the cookie below is set for it
//   more   the signed cookie ovs_web_account names one of them, and is newer than the last time an account arrived at or
//          left the IP (admin:ip_changed_at:{ip}, written by the login); else the picker page, where choosing an account
//          sends a code to that account's game (POST /account/switch) and entering it sets the cookie (/account/verify)
// The cookie is a JWT {accountId, ip} signed with the session tokens' secret, as the TS server signs it: one it set stays
// valid here.

/// <summary>The account a browser request acts on.</summary>
public static class WebAccounts
{
    public const string Cookie = "ovs_web_account";
    public static readonly TimeSpan CookieLifetime = TimeSpan.FromDays(30);
    public const string Players = "playertesters";

    /// <summary>The request's IP, as every IP-keyed record has it.</summary>
    public static string Ip(HttpContext context) => ClientAddress.Of(context, stripMapped: true);

    /// <summary>The accounts a browser at <paramref name="ip"/> may act on.</summary>
    public static FilterDefinition<BsonDocument> AtIp(string ip) => ip.Length == 0
        ? Builders<BsonDocument>.Filter.Exists("_id", false)
        : Builders<BsonDocument>.Filter.Eq("ip", ip) & Builders<BsonDocument>.Filter.Ne("provisional", true);

    /// <summary>
    /// The account the request acts on; null with no account at its IP. When several are and the cookie names none of
    /// them, the picker page has been sent (PickerShown) and the caller sends nothing more.
    /// </summary>
    public static async Task<(BsonDocument? Player, bool PickerShown)> ResolveAsync(HttpContext context, IMongoDatabase mongo, IDatabase redis, string returnTo)
    {
        string ip = Ip(context);
        var players = mongo.GetCollection<BsonDocument>(Players);
        var accounts = await players.Find(AtIp(ip)).ToListAsync(context.RequestAborted);
        if (accounts.Count == 0)
        {
            return (null, false);
        }

        if (accounts.Count == 1)
        {
            SetCookie(context, accounts[0]["_id"].AsObjectId.ToString(), ip);
            return (accounts[0], false);
        }

        if (ReadCookie(context) is { } claim && Str(claim, "ip") == ip)
        {
            if (!await StaleAsync(redis, claim, ip))
            {
                if (accounts.FirstOrDefault(a => a["_id"].AsObjectId.ToString() == Str(claim, "accountId")) is { } chosen)
                {
                    return (chosen, false);
                }
            }
            else
            {
                Log(context).LogInformation("Account cookie stale for IP {Ip} (an account arrived or left since): showing the picker", ip);
            }
        }

        await Pages.SendAsync(context, Picker(accounts, returnTo, null));
        return (null, true);
    }

    /// <summary>The picker page for these accounts.</summary>
    public static string Picker(IEnumerable<BsonDocument> accounts, string returnTo, string? error) =>
        Pages.AccountPicker(returnTo, error, accounts.Select(a =>
        {
            string hardware = Str(a, "hardwareId");
            return new Dictionary<string, object?>
            {
                ["id"] = a["_id"].AsObjectId.ToString(),
                ["name"] = Str(a, "name") is { Length: > 0 } name ? name : "Unknown",
                ["steamId"] = Str(a, "steamId"),
                ["epicId"] = Str(a, "epicId"),
                ["hardwareId"] = hardware.Length > 0 ? hardware[..Math.Min(8, hardware.Length)] + "..." : "",
            };
        }));

    /// <summary>Sets the cookie naming the account for this IP (30 days, HTTP only, SameSite lax).</summary>
    public static void SetCookie(HttpContext context, string accountId, string ip)
    {
        string secret = Secret(context);
        string token = AccessTokens.Sign(new JsonObject { ["accountId"] = accountId, ["ip"] = ip }, secret, CookieLifetime, DateTimeOffset.UtcNow);
        context.Response.Cookies.Append(Cookie, token, new CookieOptions
        {
            MaxAge = CookieLifetime,
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
        });
    }

    /// <summary>
    /// The return path a form carries, if it is one of this site's (starts with / but not // or /\, which browsers read as
    /// another host); else /home.
    /// </summary>
    public static string ReturnTo(string? value)
    {
        string path = (value ?? "").Trim();
        return path.StartsWith('/') && !path.StartsWith("//", StringComparison.Ordinal) && !path.StartsWith("/\\", StringComparison.Ordinal) ? path : "/home";
    }

    /// <summary>A form's fields (application/x-www-form-urlencoded or multipart; a JSON object too, as express reads both).</summary>
    public static async Task<Dictionary<string, string>> FieldsAsync(HttpContext context)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        if (context.Request.HasFormContentType)
        {
            foreach (var (key, value) in await context.Request.ReadFormAsync(context.RequestAborted))
            {
                fields[key] = value.ToString();
            }
        }
        else if (context.Request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) == true
            && await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted) is JsonObject json)
        {
            foreach (var (key, value) in json)
            {
                if (value is JsonValue v && v.TryGetValue(out string? text))
                {
                    fields[key] = text;
                }
            }
        }

        return fields;
    }

    private static JsonObject? ReadCookie(HttpContext context)
    {
        if (!context.Request.Cookies.TryGetValue(Cookie, out string? token) || string.IsNullOrEmpty(token))
        {
            return null;
        }

        try
        {
            return AccessTokens.Verify(token, Secret(context), DateTimeOffset.UtcNow);
        }
        catch (AccessTokenException)
        {
            return null;
        }
    }

    // Issued before the last time an account arrived at or left the IP: the household changed, choose again.
    private static async Task<bool> StaleAsync(IDatabase redis, JsonObject claim, string ip)
    {
        if (claim["iat"] is not JsonValue iat || !iat.TryGetValue(out long issuedSeconds))
        {
            return true;
        }

        var changed = await redis.StringGetAsync($"admin:ip_changed_at:{ip}");
        return changed.HasValue && double.TryParse(changed.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double changedMs)
            && issuedSeconds * 1000.0 < changedMs;
    }

    private static string Secret(HttpContext context) =>
        context.RequestServices.GetRequiredService<IOptionsMonitor<AccessSettings>>().CurrentValue.JwtSecret
        ?? throw new InvalidOperationException("Access:JwtSecret (JWT_SECRET) is not set: the account cookie cannot be signed");

    private static ILogger Log(HttpContext context) => context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(WebAccounts));

    internal static string Str(BsonDocument doc, string field) => doc.TryGetValue(field, out var value) && value.IsString ? value.AsString : "";

    private static string Str(JsonObject claim, string field) => claim[field] is JsonValue v && v.TryGetValue(out string? text) ? text : "";
}

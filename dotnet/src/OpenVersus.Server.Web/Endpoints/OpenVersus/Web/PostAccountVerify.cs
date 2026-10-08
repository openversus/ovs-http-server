using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FastEndpoints;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Web.Site;
using StackExchange.Redis;

namespace OpenVersus.Server.Web.Endpoints.OpenVersus.Web;

/// <summary>
/// POST /account/verify (form fields verifyId, code): the code from the game's banner. Right: the account cookie is set
/// and the browser goes back to where it came from; wrong: 5 tries, then the code is gone. The code is for this IP only
/// and used once. TS server: POST /account/verify (server.ts).
/// </summary>
public sealed partial class PostAccountVerify : EndpointWithoutRequest
{
    public const int MaxAttempts = 5;

    public override void Configure()
    {
        Verbs(FastEndpoints.Http.POST);
        Routes("/account/verify");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var services = Resolve<IServiceProvider>();
        if (services.GetService<IMongoDatabase>() is not { } mongo || services.GetService<IConnectionMultiplexer>() is not { } multiplexer)
        {
            await Pages.SendTextAsync(HttpContext, "", StatusCodes.Status503ServiceUnavailable);
            return;
        }

        var redis = multiplexer.GetDatabase();
        string ip = WebAccounts.Ip(HttpContext);
        var fields = await WebAccounts.FieldsAsync(HttpContext);
        string verifyId = Js.Trim(fields.GetValueOrDefault("verifyId") ?? "");
        string code = Js.Trim(fields.GetValueOrDefault("code") ?? "");
        if (!VerifyIdFormat().IsMatch(verifyId))
        {
            await Pages.SendTextAsync(HttpContext, "Invalid verify request", StatusCodes.Status400BadRequest);
            return;
        }

        string key = $"admin:verify:{verifyId}", attemptsKey = $"admin:verify:attempts:{verifyId}";
        var stored = await redis.StringGetAsync(key);
        var payload = stored.HasValue ? Js.Parse(stored.ToString()) as JsonObject : null;
        var players = mongo.GetCollection<BsonDocument>(WebAccounts.Players);
        async Task VerifyPageAsync(string error)
        {
            string accountId = Field(payload!, "accountId");
            var account = ObjectId.TryParse(accountId, out var id) ? await players.Find(Builders<BsonDocument>.Filter.Eq("_id", id)).FirstOrDefaultAsync(ct) : null;
            await Pages.SendAsync(HttpContext, Pages.AccountVerify(verifyId, Field(payload!, "returnTo") is { Length: > 0 } back ? back : "/home",
                account is not null && WebAccounts.Str(account, "name") is { Length: > 0 } name ? name : "Unknown", account is null ? "" : WebAccounts.Str(account, "steamId"), error));
        }

        if (!CodeFormat().IsMatch(code))
        {
            if (payload is not null)
            {
                await VerifyPageAsync("Code must be 6 digits.");
                return;
            }

            await Pages.SendTextAsync(HttpContext, "Invalid or expired verification. Go back and try again.", StatusCodes.Status400BadRequest);
            return;
        }

        if (payload is null)
        {
            await Pages.SendTextAsync(HttpContext, "Verification expired or already used. Go back and try again.", StatusCodes.Status400BadRequest);
            return;
        }

        // Someone else on the network who learned the verify id cannot use it.
        if (Field(payload, "ip") != ip)
        {
            await redis.KeyDeleteAsync([key, attemptsKey]);
            await Pages.SendTextAsync(HttpContext, "IP mismatch on verification. Start over from the picker.", StatusCodes.Status403Forbidden);
            return;
        }

        var tried = await redis.StringGetAsync(attemptsKey);
        int attempts = tried.HasValue && int.TryParse(tried.ToString(), out int n) ? n : 0;
        if (attempts >= MaxAttempts)
        {
            await redis.KeyDeleteAsync([key, attemptsKey]);
            await Pages.SendTextAsync(HttpContext, "Too many attempts. Start over from the picker.", StatusCodes.Status429TooManyRequests);
            return;
        }

        if (code != Field(payload, "code"))
        {
            await redis.StringIncrementAsync(attemptsKey);
            await VerifyPageAsync($"Wrong code. {MaxAttempts - attempts - 1} attempts remaining.");
            return;
        }

        await redis.KeyDeleteAsync([key, attemptsKey]);
        WebAccounts.SetCookie(HttpContext, Field(payload, "accountId"), ip);
        string returnTo = WebAccounts.ReturnTo(Field(payload, "returnTo"));
        Logger.LogInformation("Account verified: {Player} from IP {Ip} -> {ReturnTo}", Field(payload, "accountId"), ip, returnTo);
        await Send.RedirectAsync(returnTo);
    }

    private static string Field(JsonObject payload, string name) => payload[name] is JsonValue v && v.TryGetValue(out string? text) ? text : "";

    [GeneratedRegex("^[a-f0-9]{32}$", RegexOptions.IgnoreCase)]
    private static partial Regex VerifyIdFormat();

    [GeneratedRegex("^[0-9]{6}$")]
    private static partial Regex CodeFormat();
}

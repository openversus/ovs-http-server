using System.Security.Cryptography;
using System.Text.Json.Nodes;
using FastEndpoints;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Realtime;
using OpenVersus.Server.Web.Site;
using StackExchange.Redis;

namespace OpenVersus.Server.Web.Endpoints.OpenVersus.Web;

/// <summary>
/// POST /account/switch (form fields accountId, returnTo): a browser at a shared IP claims one of its accounts. The
/// account must be at this IP and in the game now; a 6-digit code goes to that game (the OpenVersus client shows it as a
/// banner, from /ovs/notifications) and the verify page asks for it (POST /account/verify). TS server: POST
/// /account/switch (server.ts). Difference: "in the game now" is the realtime gateway's online_players, not the session
/// hash, which outlives a closed game.
/// </summary>
public sealed class PostAccountSwitch : EndpointWithoutRequest
{
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);

    public override void Configure()
    {
        Verbs(FastEndpoints.Http.POST);
        Routes("/account/switch");
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
        string accountId = Js.Trim(fields.GetValueOrDefault("accountId") ?? "");
        string returnTo = WebAccounts.ReturnTo(fields.GetValueOrDefault("returnTo") ?? "/home");
        if (!ObjectId.TryParse(accountId, out var id))
        {
            await Pages.SendTextAsync(HttpContext, "Invalid accountId", StatusCodes.Status400BadRequest);
            return;
        }

        var players = mongo.GetCollection<BsonDocument>(WebAccounts.Players);
        async Task PickerAsync(string error) =>
            await Pages.SendAsync(HttpContext, WebAccounts.Picker(await players.Find(WebAccounts.AtIp(ip)).ToListAsync(ct), returnTo, error));

        var match = await players.Find(Builders<BsonDocument>.Filter.Eq("_id", id) & WebAccounts.AtIp(ip)).FirstOrDefaultAsync(ct);
        if (match is null)
        {
            await PickerAsync("That account isn't recognized on this network. Pick one from the list below.");
            return;
        }

        string name = WebAccounts.Str(match, "name") is { Length: > 0 } n ? n : "That account";
        if (!await redis.SetContainsAsync("online_players", accountId))
        {
            await PickerAsync($"{name} isn't signed in to the game right now — please log in to the main menu with that account, then click \"Use This\" again.");
            return;
        }

        string code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        string verifyId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var payload = new JsonObject { ["accountId"] = accountId, ["ip"] = ip, ["returnTo"] = returnTo, ["code"] = code, ["createdAt"] = now };
        await redis.StringSetAsync($"admin:verify:{verifyId}", Js.Stringify(payload), CodeLifetime);
        await redis.StringSetAsync($"admin:verify:attempts:{verifyId}", "0", CodeLifetime);
        // "admin_banner" is the client's two-line banner, not an administrator's action: 2 minutes on screen.
        await PlayerMessages.NotifyClientAsync(redis, accountId, "admin_banner", "Web Login Code", $"Enter {code} in the browser to verify.",
            new JsonObject { ["timeout"] = 120 }, now);
        Logger.LogInformation("Account verify code pushed to {Player} (IP {Ip}, banner 2 min, code valid 5 min)", accountId, ip);

        await Pages.SendAsync(HttpContext, Pages.AccountVerify(verifyId, returnTo, WebAccounts.Str(match, "name") is { Length: > 0 } shown ? shown : "Unknown",
            WebAccounts.Str(match, "steamId"), null));
    }
}

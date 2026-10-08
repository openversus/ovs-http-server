using System.Text.RegularExpressions;
using FastEndpoints;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Bans;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Web.Site;
using StackExchange.Redis;

namespace OpenVersus.Server.Web.Endpoints.OpenVersus.Web;

/// <summary>
/// POST /namechange (form field name): renames the account at the browser's IP. TS server: POST /namechange (server.ts),
/// with the name lists of docs/BANS.md: a banned term anywhere in the name bans the person (every identifier, their
/// connection cut); a force-change term as a word refuses the name. Then as there: cut to 24 characters, unique
/// regardless of case, saved to Mongo and the live session. Differences: a banned name is never also told it is merely
/// not permitted (the TS server overwrote the ban's message), the npm obscenity censor is gone (the lists cover it), and
/// a name change is refused while the name lists have not loaded.
/// </summary>
public sealed class PostNamechange : EndpointWithoutRequest
{
    public const int MaxNameLength = 24;
    private const string Discord = "If you think this is an error, please join the OVS Discord server at https://discord.gg/ez3Ve7eTvk and ping one of the admins.";

    public override void Configure()
    {
        Verbs(FastEndpoints.Http.POST);
        Routes("/namechange");
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
        var (player, pickerShown) = await WebAccounts.ResolveAsync(HttpContext, mongo, redis, "/namechange");
        if (pickerShown)
        {
            return;
        }

        if (player is null)
        {
            Logger.LogWarning("No player found for IP {Ip} during name change POST; the player has to connect from the game first.", ip);
            await Pages.SendAsync(HttpContext, Pages.NameChange("Unknown", "Connect to the game before changing your name.", null), StatusCodes.Status401Unauthorized);
            return;
        }

        if (!(await WebAccounts.FieldsAsync(HttpContext)).TryGetValue("name", out string? name))
        {
            await Pages.SendTextAsync(HttpContext, "Invalid IP or name format", StatusCodes.Status400BadRequest);
            return;
        }

        string id = player["_id"].AsObjectId.ToString();
        string current = WebAccounts.Str(player, "name");
        string? error = null;
        if (Js.Trim(name).Length == 0)
        {
            error = "Blank or whitespace-only names are not permitted.";
        }

        // The whole name as typed, before it is cut.
        var check = Resolve<INameRules>().Check(name);
        if (!check.Ready)
        {
            Logger.LogError("Refused a name change for player {Player}: the name lists have not loaded (see Bans:BannedNamesFile, Bans:ForceChangeNamesFile)", id);
            await Pages.SendAsync(HttpContext, Pages.NameChange(current, "Name changes are unavailable right now. Please try again in a few minutes.", null),
                StatusCodes.Status503ServiceUnavailable);
            return;
        }

        if (check.Hit is { List: NameList.Banned } banned)
        {
            error = $"The name {name} contains racial slurs, hate speech, or another banned term which is not welcome in the OVS community. You are now permanently banned from participating in matches held on OVS servers. {Discord}";
            // The request's IP is the account's: it is what found the account (WebAccounts.AtIp).
            await Resolve<IPersonBans>().BanAsync(new BanRequest(id, "banned name", "namechange", AttemptedName: name, MatchedList: banned.ListName,
                MatchedTerm: banned.Term, RequestIp: ip, UserAgent: HttpContext.Request.Headers.UserAgent.ToString()), ct);
        }
        else if (check.Hit is { List: NameList.ForceChange })
        {
            error = $"The name {name} contains a term which is not permitted in a player name. Your name has not been changed. Please refresh the page and choose a new name. {Discord}";
        }

        if (name.Length > MaxNameLength)
        {
            name = name[..MaxNameLength];
        }

        var players = mongo.GetCollection<BsonDocument>(WebAccounts.Players);
        if (error is null)
        {
            var sameName = Builders<BsonDocument>.Filter.Regex("name", new BsonRegularExpression($"^{Regex.Escape(name)}$", "i"));
            if (await players.Find(sameName & Builders<BsonDocument>.Filter.Ne("_id", player["_id"])).Limit(1).FirstOrDefaultAsync(ct) is not null)
            {
                error = $"The name \"{name}\" is already taken by another player. Please choose a different name.";
            }
        }

        if (error is null)
        {
            string trimmed = Js.Trim(name);
            await players.UpdateOneAsync(Builders<BsonDocument>.Filter.Eq("_id", player["_id"]), Builders<BsonDocument>.Update.Set("name", trimmed), cancellationToken: ct);
            try
            {
                // The live session, so the name shows at once; the IP's copy only when it is this player's.
                await redis.HashSetAsync($"connections:{id}", "username", trimmed);
                if (ip.Length > 0 && await redis.HashGetAsync($"connections:{ip}", "id") == id)
                {
                    await redis.HashSetAsync($"connections:{ip}", "username", trimmed);
                }
            }
            catch (Exception e) when (e is RedisException or TimeoutException)
            {
                // Mongo holds the name; the next login writes the session from it.
                Logger.LogError(e, "Name change: Mongo updated but the session could not be for player {Player}", id);
            }
        }

        await Pages.SendAsync(HttpContext, Pages.NameChange(error is null ? name : current, error, error is null));
        Logger.LogInformation("Name change for IP {Ip} (player {Player}) to \"{Name}\": {Result}", ip, id, name, error is null ? "done" : "refused");
    }
}

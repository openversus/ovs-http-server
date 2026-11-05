using System.Text.Json.Nodes;
using FastEndpoints;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Web.Site;
using StackExchange.Redis;

namespace OpenVersus.Server.Web.Endpoints.OpenVersus.Web;

/// <summary>
/// GET /stats: the career stats page of the account at the browser's IP (the picker when several are there, as
/// /namechange): playerstats' aggregate and ranked-set records per character, as JSON for the page's scripts
/// (my_stats.html); the no-stats page without a player or a record. TS server: GET /stats.
/// Seen in: TS server: GET /stats.
/// </summary>
public sealed class GetStats : EndpointWithoutRequest
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/stats");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (Resolve<IServiceProvider>().GetService<IMongoDatabase>() is not { } mongo || Resolve<IServiceProvider>().GetService<IConnectionMultiplexer>() is not { } redis)
        {
            await Pages.SendTextAsync(HttpContext, "", StatusCodes.Status503ServiceUnavailable);
            return;
        }

        var (player, pickerShown) = await WebAccounts.ResolveAsync(HttpContext, mongo, redis.GetDatabase(), "/stats");
        if (pickerShown)
        {
            return;
        }

        if (player is null)
        {
            await Pages.SendAsync(HttpContext, Pages.MyStats(false));
            return;
        }

        string accountId = player["_id"].ToString()!, name = WebAccounts.Str(player, "name");
        var stats = await mongo.GetCollection<BsonDocument>("playerstats").Find(new BsonDocument("account_id", accountId)).FirstOrDefaultAsync(ct);
        var aggregate = stats?.GetValue("aggregate", BsonNull.Value) is { IsBsonDocument: true } a ? a.AsBsonDocument : null;
        var c1 = stats?.GetValue("characters_1v1", BsonNull.Value) is { IsBsonDocument: true } d1 ? d1.AsBsonDocument : new BsonDocument();
        var c2 = stats?.GetValue("characters_2v2", BsonNull.Value) is { IsBsonDocument: true } d2 ? d2.AsBsonDocument : new BsonDocument();
        if (stats is null || ((aggregate is null || aggregate.ElementCount == 0) && c1.ElementCount == 0 && c2.ElementCount == 0))
        {
            await Pages.SendAsync(HttpContext, Pages.MyStats(false, name.Length > 0 ? name : ""));
            return;
        }

        // JSON for inline <script> blocks: `</` escaped so a field name can never close the tag (as the TS toScriptJson).
        static string Script(BsonValue value) => value.ToJson(new MongoDB.Bson.IO.JsonWriterSettings { OutputMode = MongoDB.Bson.IO.JsonOutputMode.RelaxedExtendedJson }).Replace("</", "<\\/");
        long updatedAt = stats.GetValue("updated_at", BsonNull.Value) is { IsNumeric: true } u && u.ToDouble() != 0 ? (long)u.ToDouble() : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await Pages.SendAsync(HttpContext, Pages.MyStats(true, name.Length > 0 ? name : "Unknown", Script(aggregate ?? new BsonDocument()),
            Script(new BsonDocument { { "1v1", c1 }, { "2v2", c2 } }), updatedAt));
    }
}

using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Compat;

namespace OpenVersus.Server.Core.Perks;

// GET /ssc/invoke/perks_get_all_pages, ported from the TS server's handleSsc_invoke_perks_get_all_pages (ssc/ssc.ts,
// branch infinity-war): the player's saved perk pages, { character: { "0": { DisplayName, Description, Perks } } }, as
// stored. A player with none, or a failed read, gets {}.
//
// Mongo, read     perkpages {account_id: ObjectId}: the first in natural order (one prod account has two), perk_pages only
//
// Unlike there: a session whose id is no ObjectId gets {} (TS: new ObjectId throws before the query and the request is
// never answered).

public interface IPerksService
{
    Task<JsonNode> PagesAsync(string accountId, CancellationToken ct);
}

internal sealed class PerksService(IServiceProvider services, ILogger<PerksService> log) : IPerksService
{
    public async Task<JsonNode> PagesAsync(string accountId, CancellationToken ct)
    {
        JsonNode pages = new JsonObject();
        try
        {
            if (ObjectId.TryParse(accountId, out var id) && services.GetService<IMongoDatabase>() is { } mongo)
            {
                var doc = await mongo.GetCollection<BsonDocument>("perkpages")
                    .Find(new BsonDocument("account_id", id))
                    .Project(new BsonDocument { { "perk_pages", 1 }, { "_id", 0 } })
                    .Limit(1)
                    .FirstOrDefaultAsync(ct);
                // doc?.perk_pages || {}
                if (doc?.GetValue("perk_pages", BsonNull.Value) is { } stored && Lean.Truthy(stored))
                {
                    pages = Lean.Value(stored)!;
                }
            }
            else if (services.GetService<IMongoDatabase>() is null)
            {
                log.LogError("Perk pages for {Account}: this service has no Mongo (MONGODB_URI); answering none", accountId);
            }
        }
        catch (Exception e) when (e is MongoException or TimeoutException or FormatException)
        {
            log.LogError("Error fetching perk pages for account {Account}, error: {Error}", accountId, e.Message);
            pages = new JsonObject();
        }

        return new JsonObject { ["body"] = new JsonObject { ["perk_pages"] = pages }, ["metadata"] = null, ["return_code"] = 0 };
    }
}

public static class PerksHosting
{
    public static WebApplicationBuilder AddPerks(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<IPerksService, PerksService>();
        return builder;
    }
}

using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Compat;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Perks;

// GET /ssc/invoke/perks_get_all_pages, ported from the TS server's handleSsc_invoke_perks_get_all_pages (ssc/ssc.ts,
// branch infinity-war): the player's saved perk pages, { character: { "0": { DisplayName, Description, Perks } } }, as
// stored. A player with none, or a failed read, gets {}.
//
// Mongo, read     perkpages {account_id: ObjectId}: the first in natural order (one prod account has two), perk_pages only
//                 (set_character_page: also the page being written, and playertesters {_id} character)
// Redis, read     connections:{id} character (set_character_page, a page with no Character)
//
// Unlike there: a session whose id is no ObjectId gets {} (TS: new ObjectId throws before the query and the request is
// never answered).
//
// PUT /ssc/invoke/perks_set_character_page {Character, PageIndex, DisplayName, Description, Perks}: one page, as the TS
// server's perks_set_page (ssc/ssc.ts) writes it: findOneAndUpdate perkpages {account_id: ObjectId} with
// {$setOnInsert: {__v: 0}, $set: {"perk_pages.<Character>.<PageIndex>": {DisplayName, Description, Perks}}} (upsert;
// perk_pages is Mixed, so the values are stored as sent, a missing one as null). Answers {body: {}}; a session id that
// is no ObjectId, or a failed write, 500 {body: {message: "Error saving perks"}, return_code: 1}.
// Unlike there, nothing is stored as null or under "undefined" (TS stores a missing field as null, and a missing
// Character or PageIndex as the key "undefined", which perks_get_all_pages then hands back to the game). A missing or
// null value (for Character and PageIndex also "") is filled in:
//   Character    the player's current character (connections:{id} character, else playertesters character), else
//                character_wonder_woman
//   PageIndex    0
//   DisplayName  the page's stored one, else "Custom Set <PageIndex + 1>" (the game's own default name; every one of
//                61,124 pages in a prod snapshot has it or a localized one, none is null)
//   Description  the page's stored one, else "" (every stored page's)
//   Perks        the page's stored ones, else [] (every stored page has four)
// A Character or PageIndex that is not text or a number, or holds "." or "$" (a Mongo path), is not written; it answers
// as a saved page does.
//
// PUT /ssc/invoke/perks_absent {ContainerMatchId}: the TS server's fixed answer, {body: {message: "Early absent
// report"}, return_code: 2}, as every captured answer was. Follow-up: docs/SSC.md.

public interface IPerksService
{
    Task<JsonNode> PagesAsync(string accountId, CancellationToken ct);

    /// <summary>perks_set_character_page: the answer and its status (200, or 500 when the page could not be saved).</summary>
    Task<(int Status, JsonObject Answer)> SetPageAsync(string accountId, JsonObject body, CancellationToken ct);
}

/// <summary>perks_absent's answer.</summary>
public static class PerksAbsent
{
    public static JsonObject Answer() =>
        new() { ["body"] = new JsonObject { ["message"] = "Early absent report" }, ["metadata"] = null, ["return_code"] = 2 };
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

    public async Task<(int Status, JsonObject Answer)> SetPageAsync(string accountId, JsonObject body, CancellationToken ct)
    {
        var saved = new JsonObject { ["body"] = new JsonObject(), ["metadata"] = null, ["return_code"] = 0 };
        var failed = new JsonObject { ["body"] = new JsonObject { ["message"] = "Error saving perks" }, ["metadata"] = null, ["return_code"] = 1 };
        if (!ObjectId.TryParse(accountId, out var id))
        {
            log.LogError("Error saving perks for account {Account}: not an ObjectId", accountId);
            return (500, failed);
        }

        string? character = Missing(body["Character"]) ? "" : PathPart(body["Character"]);
        string? index = Missing(body["PageIndex"]) ? "0" : PathPart(body["PageIndex"]);
        if (character is null || index is null)
        {
            log.LogWarning("Perk page for {Account} not saved: character {Character}, page {Page}", accountId, body["Character"]?.ToJsonString(), body["PageIndex"]?.ToJsonString());
            return (200, saved);
        }

        try
        {
            var mongo = services.GetService<IMongoDatabase>() ?? throw new InvalidOperationException("this service has no Mongo (MONGODB_URI)");
            if (character.Length == 0)
            {
                character = await CurrentCharacterAsync(mongo, accountId, id, ct);
            }

            // { DisplayName, Description, Perks }: a missing one from the stored page, else its default.
            var stored = await mongo.GetCollection<BsonDocument>("perkpages").Find(new BsonDocument("account_id", id))
                .Project(new BsonDocument($"perk_pages.{character}.{index}", 1)).FirstOrDefaultAsync(ct);
            var storedPage = stored?.GetValue("perk_pages", BsonNull.Value) is BsonDocument pages && pages.GetValue(character, BsonNull.Value) is BsonDocument byIndex
                && byIndex.GetValue(index, BsonNull.Value) is BsonDocument p ? p : null;
            // Absent or null only: an empty DisplayName or Description is a value the player chose.
            BsonValue Field(string name, BsonValue fallback) =>
                body[name] is not null ? ToBson(body[name]) : storedPage?.GetValue(name, BsonNull.Value) is { IsBsonNull: false } kept ? kept : fallback;
            var page = new BsonDocument
            {
                { "DisplayName", Field("DisplayName", $"Custom Set {(int.TryParse(index, out int n) && n < int.MaxValue ? (n + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : "1")}") },
                { "Description", Field("Description", "") },
                { "Perks", Field("Perks", new BsonArray()) },
            };

            await mongo.GetCollection<BsonDocument>("perkpages").FindOneAndUpdateAsync(
                new BsonDocument("account_id", id),
                new BsonDocumentUpdateDefinition<BsonDocument>(new BsonDocument
                {
                    { "$setOnInsert", new BsonDocument("__v", 0) },
                    { "$set", new BsonDocument($"perk_pages.{character}.{index}", page) },
                }),
                new FindOneAndUpdateOptions<BsonDocument> { IsUpsert = true, ReturnDocument = ReturnDocument.After },
                ct);
        }
        catch (Exception e) when (e is MongoException or TimeoutException or InvalidOperationException)
        {
            log.LogError("Error saving perks for account {Account}, error: {Error}", accountId, e.Message);
            return (500, failed);
        }

        return (200, saved);
    }

    // Absent, null or "" (a Character or PageIndex: neither can be an empty path segment).
    private static bool Missing(JsonNode? value) => value is null || (value is JsonValue v && v.TryGetValue(out string? s) && s.Length == 0);

    // The player's current character: their connection's, else their record's, else the default (as ranked_data picks it).
    private async Task<string> CurrentCharacterAsync(IMongoDatabase mongo, string accountId, ObjectId id, CancellationToken ct)
    {
        if (services.GetService<IConnectionMultiplexer>()?.GetDatabase() is { } redis
            && await redis.HashGetAsync($"connections:{accountId}", "character") is { HasValue: true } connected && PathPart(JsonValue.Create(connected.ToString())) is { } fromConnection)
        {
            return fromConnection;
        }

        var player = await mongo.GetCollection<BsonDocument>("playertesters").Find(new BsonDocument("_id", id))
            .Project(new BsonDocument("character", 1)).FirstOrDefaultAsync(ct);
        return player?.GetValue("character", BsonNull.Value) is BsonString s && PathPart(JsonValue.Create(s.Value)) is { } fromRecord ? fromRecord : DefaultCharacter;
    }

    private const string DefaultCharacter = "character_wonder_woman";

    // A path segment as JS writes the value into a template string: text as it is, a number as JS prints it; refused when
    // missing, empty, not text or a number, or holding "." or "$".
    private static string? PathPart(JsonNode? value)
    {
        string? text = value switch
        {
            JsonValue v when v.TryGetValue(out string? s) => s,
            JsonValue v when v.GetValueKind() == System.Text.Json.JsonValueKind.Number => Js.Stringify(JsonValue.Create(double.Parse(v.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture))),
            _ => null,
        };
        return string.IsNullOrEmpty(text) || text.Contains('.') || text.Contains('$') ? null : text;
    }

    // A JSON value as the BSON serializer stores it.
    private static BsonValue ToBson(JsonNode? value) => value is null ? BsonNull.Value : BsonDocument.Parse($"{{\"v\":{Js.Stringify(value)}}}")["v"];
}

public static class PerksHosting
{
    public static WebApplicationBuilder AddPerks(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<IPerksService, PerksService>();
        return builder;
    }
}

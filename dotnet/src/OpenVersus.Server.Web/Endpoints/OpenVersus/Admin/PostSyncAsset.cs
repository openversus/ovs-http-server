using System.Text.Json.Nodes;
using FastEndpoints;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Web.Site;
using StackExchange.Redis;
using MongoDB.Bson.Serialization;

namespace OpenVersus.Server.Web.Endpoints.OpenVersus.Admin;

/// <summary>
/// POST /syncAsset {assetType, assetPath, slug, enabled, character_slug}: an asset upserted into dataassets by its path
/// (the admin tooling's sync), then the config CRC bumped so every client refetches the hiss (the hiss rebuilds per CRC).
/// 403 without "Authorization: Bearer <Admin:DataAssetToken>"; 200 as the TS route; 404 with the error's name on a failure.
/// Seen in: TS server: POST /syncAsset.
/// </summary>
public sealed class PostSyncAsset : EndpointWithoutRequest
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.POST);
        Routes("/syncAsset");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var settings = Resolve<Microsoft.Extensions.Options.IOptionsMonitor<AdminSettings>>().CurrentValue;
        Logger.LogInformation("Trying syncAsset");
        if (settings.DataAssetToken.Length == 0 || HttpContext.Request.Headers.Authorization.ToString() != $"Bearer {settings.DataAssetToken}")
        {
            HttpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
            await HttpContext.Response.StartAsync(ct);
            return;
        }

        try
        {
            var dto = await JsonNode.ParseAsync(HttpContext.Request.Body, cancellationToken: ct) as JsonObject ?? throw new InvalidOperationException("no body");
            var mongo = Resolve<IServiceProvider>().GetService<IMongoDatabase>() ?? throw new InvalidOperationException("this service has no Mongo (MONGODB_URI)");
            static BsonValue Of(JsonNode? n) => n is null ? BsonNull.Value : BsonSerializer.Deserialize<BsonValue>(n.ToJsonString());
            string assetPath = dto["assetPath"] is JsonValue v && v.TryGetValue(out string? s) ? s : "";
            await mongo.GetCollection<BsonDocument>("dataassets").UpdateOneAsync(new BsonDocument("assetPath", assetPath),
                new BsonDocument("$set", new BsonDocument
                {
                    { "slug", Of(dto["slug"]) }, { "assetType", Of(dto["assetType"]) }, { "character_slug", Of(dto["character_slug"]) },
                    { "enabled", Of(dto["enabled"]) }, { "assetPath", assetPath },
                }), new UpdateOptions { IsUpsert = true }, ct);
            // UpdateCrc: the single config document's CRC + 1 (made when missing).
            await mongo.GetCollection<BsonDocument>("config").UpdateOneAsync(FilterDefinition<BsonDocument>.Empty,
                new BsonDocument("$inc", new BsonDocument("CRC", 1)), new UpdateOptions { IsUpsert = true }, ct);
            Logger.LogInformation("Asset {Path} synced; the config CRC is bumped", assetPath);
            HttpContext.Response.StatusCode = StatusCodes.Status200OK;
            await HttpContext.Response.StartAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Logger.LogError("syncAsset failed: {Error}", e.Message);
            await Pages.SendTextAsync(HttpContext, (e as MongoCommandException)?.CodeName ?? "", StatusCodes.Status404NotFound);
        }
    }
}

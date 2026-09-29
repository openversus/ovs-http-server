using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace OpenVersus.Server.Core.Leaderboards;

// A player's ranked score and place, ported from the TS server's PUT /leaderboards/bulk/score-and-rank/:playerId
// (server.ts) and getPlayerRank (services/eloService.ts), branch infinity-war.
//
// Mongo, read   eloratings { account_id, elo_1v1, wins_1v1, losses_1v1, elo_2v2, wins_2v2, losses_2v2 }
// Nothing written.

public interface IRankService
{
    /// <summary>
    /// The score-and-rank answer for <paramref name="playerId"/>: <c>{body: {"1v1": {score, rank}, "2v2": ...}, metadata:
    /// null, return_code: 200}</c>; a mode the player has no games in is score 1000, rank 0.
    /// </summary>
    Task<JsonObject> ScoreAndRankAsync(string playerId, CancellationToken ct = default);
}

internal sealed class RankService(IServiceProvider services, ILogger<RankService> log) : IRankService
{
    public async Task<JsonObject> ScoreAndRankAsync(string playerId, CancellationToken ct)
    {
        JsonObject body;
        try
        {
            var ratings = services.GetService<IMongoDatabase>()?.GetCollection<BsonDocument>("eloratings")
                ?? throw new InvalidOperationException("this service has no Mongo (MONGODB_URI)");
            body = new JsonObject
            {
                ["1v1"] = await ScoreAsync(ratings, playerId, "1v1", ct),
                ["2v2"] = await ScoreAsync(ratings, playerId, "2v2", ct),
            };
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log.LogError(e, "Error in leaderboards/bulk/score-and-rank for {Player}", playerId);
            body = [];
        }

        return new JsonObject { ["body"] = body, ["metadata"] = null, ["return_code"] = 200 };
    }

    /// <summary>getPlayerRank: the player's rating and place among the players with games in that mode.</summary>
    private static async Task<JsonObject> ScoreAsync(IMongoCollection<BsonDocument> ratings, string playerId, string mode, CancellationToken ct)
    {
        if (await PlaceAsync(ratings, playerId, mode, ct) is not { } place)
        {
            return new JsonObject { ["score"] = 1000, ["rank"] = 0 };
        }

        return new JsonObject { ["score"] = Json(place.Rating), ["rank"] = place.Rank };
    }

    /// <summary>
    /// The player's rating and place in <paramref name="mode"/> (TS getPlayerRank without a character): null when they
    /// have no rating or no games in it.
    /// </summary>
    internal static async Task<(BsonValue Rating, long Rank)?> PlaceAsync(IMongoCollection<BsonDocument> ratings, string playerId, string mode, CancellationToken ct)
    {
        string elo = $"elo_{mode}", wins = $"wins_{mode}", losses = $"losses_{mode}";
        var player = await ratings.Find(new BsonDocument("account_id", playerId)).FirstOrDefaultAsync(ct);
        if (player is null || Number(player, wins) + Number(player, losses) == 0)
        {
            return null;
        }

        // Players listed above: a higher rating, or the same rating and an earlier _id (the leaderboard's tie order).
        var rating = player.GetValue(elo, BsonNull.Value);
        long above = await ratings.CountDocumentsAsync(new BsonDocument
        {
            { "$or", new BsonArray
                {
                    new BsonDocument(elo, new BsonDocument("$gt", rating)),
                    new BsonDocument { { elo, rating }, { "_id", new BsonDocument("$lt", player["_id"]) } },
                }
            },
            { "$expr", new BsonDocument("$gt", new BsonArray { new BsonDocument("$add", new BsonArray { $"${wins}", $"${losses}" }), 0 }) },
        }, cancellationToken: ct);
        return (rating, above + 1);
    }

    // player[field] || 0
    internal static double Number(BsonDocument doc, string field) =>
        doc.GetValue(field, BsonNull.Value) is { IsNumeric: true } v ? v.ToDouble() : 0;

    internal static JsonNode? Json(BsonValue value) => value.BsonType switch
    {
        BsonType.Int32 => value.AsInt32,
        BsonType.Int64 => value.AsInt64,
        BsonType.Double => value.AsDouble,
        BsonType.Null => null,
        _ => throw new FormatException($"a {value.BsonType} rating"),
    };
}

public static class RankHosting
{
    public static WebApplicationBuilder AddRanks(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<IRankService, RankService>();
        return builder;
    }
}

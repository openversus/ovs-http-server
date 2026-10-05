using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Settings;

namespace OpenVersus.Server.Core.Leaderboards;

/// <summary>
/// A player's rating (eloratings {account_id}), made when missing: the TS server's getOrCreateRating
/// (services/eloService.ts). Written: an insert for a player with no rating (_id, account_id, username, elo_1v1, elo_2v2
/// (Ranked:DefaultElo), wins/losses_1v1/2v2 0, win_streak_1v1/2v2 0, updated_at (ms, a double), __v 0; mongoose leaves the
/// empty characters maps out); updateOne {account_id} $set username when a non-empty one differs from the stored one.
/// </summary>
public sealed class EloRatings(IServiceProvider services, IOptionsMonitor<RankedSettings> settings, TimeProvider time, ILogger<EloRatings> log)
{
    /// <summary>The player's rating, made when missing; null when this service has no Mongo.</summary>
    public async Task<BsonDocument?> GetOrCreateAsync(string playerId, string username, CancellationToken ct) =>
        services.GetService<IMongoDatabase>() is { } mongo ? await GetOrCreateAsync(mongo.GetCollection<BsonDocument>("eloratings"), playerId, username, ct) : null;

    public async Task<BsonDocument> GetOrCreateAsync(IMongoCollection<BsonDocument> ratings, string playerId, string username, CancellationToken ct)
    {
        var filter = new BsonDocument("account_id", playerId);
        var rating = await ratings.Find(filter).FirstOrDefaultAsync(ct);
        if (rating is null)
        {
            var elo = JsNumber(settings.CurrentValue.DefaultElo);
            rating = new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() },
                { "account_id", playerId },
                { "username", username },
                { "elo_1v1", elo },
                { "elo_2v2", elo },
                { "wins_1v1", 0 },
                { "losses_1v1", 0 },
                { "wins_2v2", 0 },
                { "losses_2v2", 0 },
                { "win_streak_1v1", 0 },
                { "win_streak_2v2", 0 },
                { "updated_at", (double)time.GetUtcNow().ToUnixTimeMilliseconds() },
                { "__v", 0 },
            };
            // Two first requests at once: the unique index refuses the second, which falls back, as there.
            await ratings.InsertOneAsync(rating, cancellationToken: ct);
            log.LogInformation("Created new ELO rating for player {Player} ({Username})", playerId, username);
        }
        else if (username.Length > 0 && username != (rating.GetValue("username", "") is BsonString stored ? stored.Value : ""))
        {
            await ratings.UpdateOneAsync(filter, new BsonDocument("$set", new BsonDocument("username", username)), cancellationToken: ct);
        }

        return rating;
    }

    /// <summary>A JS number as the node driver stores it: an int32 when it is a whole number in range, else a double.</summary>
    internal static BsonValue JsNumber(double value) =>
        value == Math.Floor(value) && value is >= int.MinValue and <= int.MaxValue && !(value == 0 && double.IsNegative(value)) ? new BsonInt32((int)value) : new BsonDouble(value);
}

public static class EloRatingsHosting
{
    /// <summary>The ratings and the setting they start from; once however many services ask.</summary>
    public static WebApplicationBuilder AddEloRatings(this WebApplicationBuilder builder)
    {
        if (builder.Services.Any(d => d.ServiceType == typeof(EloRatings)))
        {
            return builder;
        }

        builder.AddSetting<RankedSettings>("Ranked");
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<EloRatings>();
        return builder;
    }
}

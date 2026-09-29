using MongoDB.Bson;
using MongoDB.Driver;

namespace OpenVersus.Server.Core.Access;

/// <summary>
/// The daily toast bonus (data/playerCounters.ts): +10 match_toasts once per day, claimable at the first login after
/// 11:00 America/Chicago. The playercounters document is created on first use with 100 toasts. The grant is one
/// conditional update, so concurrent logins (retries, replicas) cannot grant twice.
/// </summary>
internal static class DailyToastBonus
{
    public const string Collection = "playercounters";
    public const int Bonus = 10;
    public const int StartingToasts = 100;

    private static readonly TimeZoneInfo s_chicago = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");

    /// <summary>Unix seconds of the most recent 11:00 in Chicago: today's once it has passed, else yesterday's.</summary>
    public static long MostRecentBoundary(DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(now, s_chicago);
        var day = DateOnly.FromDateTime(local.DateTime);
        if (local.Hour < 11)
        {
            day = day.AddDays(-1);
        }

        var eleven = day.ToDateTime(new TimeOnly(11, 0), DateTimeKind.Unspecified);
        return new DateTimeOffset(eleven, s_chicago.GetUtcOffset(eleven)).ToUnixTimeSeconds();
    }

    /// <summary>Grants the bonus when due. Returns the toasts granted (0 or <see cref="Bonus"/>) and the new count.</summary>
    public static async Task<(int Granted, long Count)> TryGrantAsync(IMongoDatabase mongo, string accountId, DateTimeOffset now, CancellationToken ct)
    {
        var counters = mongo.GetCollection<BsonDocument>(Collection);
        long boundary = MostRecentBoundary(now);
        var at = now.UtcDateTime;
        var after = new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After };

        // Create with the defaults on first use. The fields and the timestamps are the ones mongoose writes (the TS
        // model has timestamps on and a version key), so the TS services read the same document.
        var current = await counters.FindOneAndUpdateAsync(
            new BsonDocument("accountId", accountId),
            new BsonDocument
            {
                { "$set", new BsonDocument("updatedAt", at) },
                { "$setOnInsert", new BsonDocument { { "accountId", accountId }, { "match_toasts", StartingToasts }, { "lastToastBonusUnix", 0 }, { "__v", 0 }, { "createdAt", at } } },
            },
            new FindOneAndUpdateOptions<BsonDocument> { IsUpsert = true, ReturnDocument = ReturnDocument.After },
            ct);

        var granted = await counters.FindOneAndUpdateAsync(
            new BsonDocument { { "accountId", accountId }, { "lastToastBonusUnix", new BsonDocument("$lt", Number(boundary)) } },
            new BsonDocument
            {
                { "$set", new BsonDocument { { "lastToastBonusUnix", Number(boundary) }, { "updatedAt", at } } },
                { "$inc", new BsonDocument("match_toasts", Bonus) },
            },
            after,
            ct);

        // Already claimed: the count read above is current (the TS server reads it a third time).
        return granted is null ? (0, current["match_toasts"].ToInt64()) : (Bonus, granted["match_toasts"].ToInt64());
    }

    // As the TS server's driver stores a JavaScript number: a 32-bit integer when it fits.
    private static BsonValue Number(long n) => n is >= int.MinValue and <= int.MaxValue ? new BsonInt32((int)n) : new BsonDouble(n);
}

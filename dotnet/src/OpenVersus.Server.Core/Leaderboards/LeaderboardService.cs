using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Access;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Leaderboards;

// The ranked leaderboard screens, ported from the TS server's GET /leaderboards/:slug/show and
// /leaderboards/:slug/around/:playerId (server.ts), services/leaderboardShow.ts and getLeaderboard /
// getCharacterLeaderboard / getPlayerCharacterRank (services/eloService.ts), branch infinity-war.
//
// Mongo, read     eloratings (elo_*, wins_*, losses_*, characters_*: {slug: {elo, wins, losses}}, username),
//                 playertesters name (a rating without a username)
// Redis, read     connections:{id} username (the same)
// Mongo, written  eloratings username: a board of all characters stores the name it found for a rating that had none
//                 (or "Unknown")

/// <summary>A leaderboard request's query: <c>count</c>, and the repeated <c>fields</c> / <c>account_fields</c>.</summary>
public sealed record LeaderboardQuery(string? Count, IReadOnlyList<string> Fields, IReadOnlyList<string> AccountFields);

public interface ILeaderboardService
{
    /// <summary>The top of a board (<c>ranked_season5_1v1_all</c>, <c>..._2v2_character_shaggy</c>): <c>{leaders: [...]}</c>.</summary>
    Task<JsonObject> ShowAsync(string slug, LeaderboardQuery query, CancellationToken ct = default);

    /// <summary>The rows around <paramref name="playerId"/>'s place (5 above, 5 below); none when they are not on it.</summary>
    Task<JsonObject> AroundAsync(string slug, string playerId, LeaderboardQuery query, CancellationToken ct = default);

    /// <summary>
    /// GET /ssc/invoke/get_gm_leaderboards (the TS handleSsc_invoke_get_gm_leaderboards): the top 100 of 1v1 and of
    /// 2v2 as <c>{OneVsOne: [{Rank, Score, AccountId, CharacterSlug}], TwoVsTwo: [...]}</c>, the character being the
    /// one the player is connected with (connections:{id}, else Wonder Woman); both lists empty when a read fails.
    /// </summary>
    Task<JsonObject> GmLeaderboardsAsync(CancellationToken ct = default);
}

internal sealed partial class LeaderboardService(IServiceProvider services, ILogger<LeaderboardService> log) : ILeaderboardService
{
    private const int AroundWindow = 5;

    private sealed record Row(long Rank, string AccountId, string Username, BsonValue Elo, string BestCharacter);

    public Task<JsonObject> ShowAsync(string slug, LeaderboardQuery query, CancellationToken ct) => Guarded("show", async ratings =>
    {
        var (mode, character) = ParseSlug(slug);
        // Math.min(parseInt(count) || 100, 100)
        int count = Math.Min(JsParseInt(query.Count) is { } n && n != 0 ? n : 100, 100);
        return Body(await RowsAsync(ratings, mode, character, count, 0, ct), query);
    }, ct);

    public Task<JsonObject> AroundAsync(string slug, string playerId, LeaderboardQuery query, CancellationToken ct) => Guarded("around", async ratings =>
    {
        var (mode, character) = ParseSlug(slug);
        long? rank = character is null
            ? (await RankService.PlaceAsync(ratings, playerId, mode, ct))?.Rank
            : await CharacterRankAsync(ratings, mode, character, playerId, ct);
        var rows = rank is { } r ? await RowsAsync(ratings, mode, character, AroundWindow * 2 + 1, (int)Math.Max(0, r - 1 - AroundWindow), ct) : [];
        return Body(rows, query);
    }, ct);

    private const string DefaultCharacter = "character_wonder_woman";

    public Task<JsonObject> GmLeaderboardsAsync(CancellationToken ct) => Guarded("gm", async ratings =>
    {
        var redis = services.GetService<IConnectionMultiplexer>()?.GetDatabase();
        return new JsonObject
        {
            ["OneVsOne"] = await GmEntriesAsync(await RowsAsync(ratings, "1v1", null, 100, 0, ct), redis),
            ["TwoVsTwo"] = await GmEntriesAsync(await RowsAsync(ratings, "2v2", null, 100, 0, ct), redis),
        };
    }, ct, () => new JsonObject { ["OneVsOne"] = new JsonArray(), ["TwoVsTwo"] = new JsonArray() });

    private static async Task<JsonArray> GmEntriesAsync(List<Row> rows, IDatabase? redis)
    {
        var entries = new JsonArray();
        foreach (var row in rows)
        {
            string character = DefaultCharacter;
            try
            {
                // The TS handler's try/catch around the hash read: any failure leaves the default.
                if (redis is not null && (string?)await redis.HashGetAsync($"connections:{row.AccountId}", "character") is { Length: > 0 } live)
                {
                    character = live;
                }
            }
            catch (RedisException)
            {
            }

            entries.Add(new JsonObject { ["Rank"] = row.Rank, ["Score"] = RankService.Json(row.Elo), ["AccountId"] = row.AccountId, ["CharacterSlug"] = character });
        }

        return entries;
    }

    private async Task<JsonObject> Guarded(string view, Func<IMongoCollection<BsonDocument>, Task<JsonObject>> read, CancellationToken ct, Func<JsonObject>? empty = null)
    {
        try
        {
            var ratings = services.GetService<IMongoDatabase>()?.GetCollection<BsonDocument>("eloratings")
                ?? throw new InvalidOperationException("this service has no Mongo (MONGODB_URI)");
            return await read(ratings);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log.LogError(e, "Leaderboard {View} failed", view);
            return empty?.Invoke() ?? new JsonObject { ["leaders"] = new JsonArray() };
        }
    }

    /// <summary>"ranked_season5_1v1_all" -> 1v1, every character; "..._1v1_character_shaggy" -> that character.</summary>
    internal static (string Mode, string? Character) ParseSlug(string slug)
    {
        var match = SlugPattern().Match(slug);
        string mode = match.Success ? match.Groups[1].Value : slug.Contains("2v2", StringComparison.Ordinal) ? "2v2" : "1v1";
        string? scope = match.Success && match.Groups[2].Success ? match.Groups[2].Value : null;
        return (mode, string.IsNullOrEmpty(scope) || scope == "all" ? null : scope);
    }

    [GeneratedRegex(@"_(1v1|2v2)(?:_(.+))?$")]
    private static partial Regex SlugPattern();

    private async Task<List<Row>> RowsAsync(IMongoCollection<BsonDocument> ratings, string mode, string? character, int limit, int skip, CancellationToken ct)
    {
        var rows = new List<Row>();
        if (character is null)
        {
            string elo = $"elo_{mode}";
            var players = await ratings.Find(GamesPlayed($"wins_{mode}", $"losses_{mode}"))
                .Sort(new BsonDocument { { elo, -1 }, { "_id", 1 } }).Skip(skip).Limit(limit).ToListAsync(ct);
            for (int i = 0; i < players.Count; i++)
            {
                var p = players[i];
                string accountId = p.GetValue("account_id", BsonNull.Value).ToString()!;
                string username = await UsernameAsync(p, accountId, backfill: true, ratings, ct);
                rows.Add(new Row(skip + i + 1, accountId, username, p.GetValue(elo, BsonNull.Value), BestCharacter(p, mode)));
            }
        }
        else
        {
            var pipeline = CharacterPipeline(mode, character).Concat([new BsonDocument("$skip", skip), new BsonDocument("$limit", limit), Projection]);
            var players = await ratings.Aggregate<BsonDocument>(pipeline.ToArray(), cancellationToken: ct).ToListAsync(ct);
            for (int i = 0; i < players.Count; i++)
            {
                var p = players[i];
                string accountId = p.GetValue("account_id", BsonNull.Value).ToString()!;
                rows.Add(new Row(skip + i + 1, accountId, await UsernameAsync(p, accountId, backfill: false, ratings, ct), p["charElo"], character));
            }
        }

        return rows;
    }

    private static async Task<long?> CharacterRankAsync(IMongoCollection<BsonDocument> ratings, string mode, string character, string playerId, CancellationToken ct)
    {
        var ranked = await ratings.Aggregate<BsonDocument>(CharacterPipeline(mode, character).Concat([Projection]).ToArray(), cancellationToken: ct).ToListAsync(ct);
        int index = ranked.FindIndex(r => r.GetValue("account_id", BsonNull.Value) == playerId);
        return index < 0 ? null : index + 1;
    }

    // $expr: wins + losses > 0
    private static BsonDocument GamesPlayed(string wins, string losses) =>
        new("$expr", new BsonDocument("$gt", new BsonArray { new BsonDocument("$add", new BsonArray { $"${wins}", $"${losses}" }), 0 }));

    private static readonly BsonDocument Projection = new("$project", new BsonDocument { { "account_id", 1 }, { "username", 1 }, { "charElo", 1 }, { "charWins", 1 }, { "charLosses", 1 } });

    /// <summary>The TS pipeline: each rating's entry for the character (its name compared in lower case), by that elo.</summary>
    private static IEnumerable<BsonDocument> CharacterPipeline(string mode, string character)
    {
        string characters = $"$characters_{mode}";
        return
        [
            new("$set", new BsonDocument("_charArray", new BsonDocument("$objectToArray", new BsonDocument("$ifNull", new BsonArray { characters, new BsonDocument() })))),
            new("$set", new BsonDocument("_charEntry", new BsonDocument("$first", new BsonDocument("$filter", new BsonDocument
            {
                { "input", "$_charArray" }, { "as", "c" },
                { "cond", new BsonDocument("$eq", new BsonArray { new BsonDocument("$toLower", "$$c.k"), character.ToLowerInvariant() }) },
            })))),
            new("$match", new BsonDocument("_charEntry", new BsonDocument("$ne", BsonNull.Value))),
            new("$set", new BsonDocument
            {
                { "charElo", new BsonDocument("$ifNull", new BsonArray { "$_charEntry.v.elo", 0 }) },
                { "charWins", new BsonDocument("$ifNull", new BsonArray { "$_charEntry.v.wins", 0 }) },
                { "charLosses", new BsonDocument("$ifNull", new BsonArray { "$_charEntry.v.losses", 0 }) },
            }),
            new("$match", GamesPlayed("charWins", "charLosses")),
            new("$sort", new BsonDocument { { "charElo", -1 }, { "_id", 1 } }),
        ];
    }

    /// <summary>
    /// The rating's username; when it has none (or "Unknown"): the player's session name, else their player record's
    /// name, stored back into the rating on a board of all characters. "Unknown" when nothing has one.
    /// </summary>
    private async Task<string> UsernameAsync(BsonDocument rating, string accountId, bool backfill, IMongoCollection<BsonDocument> ratings, CancellationToken ct)
    {
        string? username = rating.GetValue("username", BsonNull.Value) is { IsString: true } u ? u.AsString : null;
        if (!string.IsNullOrEmpty(username) && username != "Unknown")
        {
            return username;
        }

        username = services.GetService<IConnectionMultiplexer>()?.GetDatabase() is { } redis
            ? (string?)await redis.HashGetAsync($"connections:{accountId}", "username")
            : null;
        if (string.IsNullOrEmpty(username) && ObjectId.TryParse(accountId, out var id))
        {
            var player = await services.GetRequiredService<IMongoDatabase>().GetCollection<BsonDocument>(PlayerRecord.Collection)
                .Find(new BsonDocument("_id", id)).FirstOrDefaultAsync(ct);
            username = player?.GetValue("name", BsonNull.Value) is { IsString: true } name ? name.AsString : "";
        }

        if (backfill && !string.IsNullOrEmpty(username))
        {
            await ratings.UpdateOneAsync(new BsonDocument("account_id", accountId), new BsonDocument("$set", new BsonDocument("username", username)), cancellationToken: ct);
        }

        return string.IsNullOrEmpty(username) ? "Unknown" : username;
    }

    // The character with the highest elo in the mode ("" for none); the first wins a tie.
    private static string BestCharacter(BsonDocument rating, string mode)
    {
        string best = "";
        double bestElo = -1;
        if (rating.GetValue($"characters_{mode}", BsonNull.Value) is BsonDocument characters)
        {
            foreach (var entry in characters)
            {
                double elo = entry.Value is BsonDocument data ? RankService.Number(data, "elo") : 0;
                if (elo > bestElo)
                {
                    bestElo = elo;
                    best = entry.Name;
                }
            }
        }

        return best;
    }

    /// <summary>buildLeaderboardShowBody: each row with the requested flattened fields added, in the order asked.</summary>
    private static JsonObject Body(List<Row> rows, LeaderboardQuery query)
    {
        var leaders = new JsonArray();
        foreach (var row in rows)
        {
            var profile = new JsonObject { ["id"] = row.AccountId, ["account_id"] = row.AccountId };
            foreach (string field in query.Fields)
            {
                profile[field] = FieldValue(field, row);
            }

            var account = new JsonObject { ["id"] = row.AccountId, ["public_id"] = row.AccountId, ["identity.username"] = row.Username };
            foreach (string field in query.AccountFields)
            {
                account[field] = FieldValue(field, row);
            }

            leaders.Add(new JsonObject { ["id"] = row.AccountId, ["rank"] = row.Rank, ["score"] = RankService.Json(row.Elo), ["account"] = account, ["profile"] = profile });
        }

        return new JsonObject { ["leaders"] = leaders };
    }

    private static JsonNode? FieldValue(string field, Row row) =>
        field.EndsWith(".CharacterSlug", StringComparison.Ordinal) ? row.BestCharacter
        : field == "identity.username" ? row.Username
        : null;

    /// <summary>JavaScript parseInt: optional space and sign, then leading decimal digits; null for none (NaN).</summary>
    internal static int? JsParseInt(string? text)
    {
        var match = text is null ? null : Regex.Match(text, @"^\s*([+-]?\d+)");
        return match is { Success: true } && long.TryParse(match.Groups[1].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long n)
            ? (int)Math.Clamp(n, int.MinValue, int.MaxValue)
            : null;
    }
}

public static class LeaderboardHosting
{
    public static WebApplicationBuilder AddLeaderboards(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<ILeaderboardService, LeaderboardService>();
        return builder;
    }
}

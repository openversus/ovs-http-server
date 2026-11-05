using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Hiss;
using OpenVersus.Server.Core.Leaderboards;
using OpenVersus.Server.Core.Hosting;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Tests.Leaderboards;

/// <summary>
/// The end-of-season rewards claim and the flag ranked_data answers with (the TS server has neither: its answer always
/// says not granted). Real Redis (database 15) and Mongo (a database of its own, dropped): OVS_TEST_REDIS,
/// OVS_TEST_REDIS_USER, OVS_TEST_REDIS_PW, OVS_TEST_MONGO, as OpsTests.
/// </summary>
[Collection(RedisTestDatabase.Name)]
public sealed class EndOfSeasonRewardsTests : IAsyncLifetime
{
    private const int TestRedisDb = 15;
    private const string TestMongoDb = "ovs_ranked_claim_tests";
    private static readonly string? s_redis = Environment.GetEnvironmentVariable("OVS_TEST_REDIS");
    private static readonly string? s_mongo = Environment.GetEnvironmentVariable("OVS_TEST_MONGO");

    private readonly List<string> _ids = [];
    private WebApplication? _app;
    private IMongoClient? _seedMongo;
    private IRankedDataService Ranked => _app!.Services.GetRequiredService<IRankedDataService>();
    private IMongoCollection<BsonDocument> Claims => _seedMongo!.GetDatabase(TestMongoDb).GetCollection<BsonDocument>(RankedDataService.ClaimsCollection);
    private IDatabase Redis => _app!.Services.GetRequiredService<IConnectionMultiplexer>().GetDatabase(TestRedisDb);

    private static bool Configured => !string.IsNullOrEmpty(s_redis) && !string.IsNullOrEmpty(s_mongo);


    private string NewPlayer()
    {
        string id = ObjectId.GenerateNewId().ToString();
        _ids.Add(id);
        return id;
    }

    public async Task InitializeAsync()
    {
        if (!Configured)
        {
            return;
        }

        string[] parts = s_redis!.Split(':');
        var mongoUrl = new MongoUrlBuilder(s_mongo) { DatabaseName = TestMongoDb };
        _seedMongo = new MongoClient(mongoUrl.ToMongoUrl());
        await _seedMongo.DropDatabaseAsync(TestMongoDb);
        var builder = OpenVersusHost.CreateBuilder(new ServiceDefinition("rankedtest", "TEST_PORT", 1, 1),
        [
            "--TEST_PORT=0", "--Control:Port=0", "--Control:Socket=off",
            $"--REDIS={parts[0]}", $"--REDIS_PORT={(parts.Length > 1 ? parts[1] : "6379")}",
            $"--REDIS_USERNAME={Environment.GetEnvironmentVariable("OVS_TEST_REDIS_USER") ?? ""}",
            $"--REDIS_PW={Environment.GetEnvironmentVariable("OVS_TEST_REDIS_PW") ?? ""}",
            $"--REDIS_DB={TestRedisDb}", $"--MONGODB_URI={mongoUrl.ToMongoUrl()}",
        ]);
        builder.AddRankedData();
        _app = builder.Build();
        _app.UseOpenVersus();
        await _app.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            foreach (string id in _ids)
            {
                await Redis.KeyDeleteAsync($"connections:{id}");
            }

            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        if (_seedMongo is not null)
        {
            await _seedMongo.DropDatabaseAsync(TestMongoDb);
        }
    }

    private async Task<bool> GrantedAsync(string id)
    {
        var answer = await Ranked.DataAsync(new JsonObject { ["id"] = id, ["username"] = "p" }, default);
        return answer["body"]!["SeasonalData"]!["Season:SeasonFive"]!["Ranked"]!["bEndOfSeasonRewardsGranted"]!.GetValue<bool>();
    }

    [SkippableFact]
    public async Task AClaimedSeasonIsGrantedForThatPlayerOnly()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        string player = NewPlayer(), other = NewPlayer();
        Assert.False(await GrantedAsync(player));

        var answer = await Ranked.ClaimRewardsAsync(player, "Season:SeasonFive", default);
        // The TS catch-all's answer, which the game has always had.
        Assert.Equal(200, answer["return_code"]!.GetValue<int>());
        Assert.Equal(new HissSettings().MatchmakingCrc, answer["body"]!["MatchmakingCrc"]!.GetValue<int>());
        Assert.NotNull(answer["body"]!["Crc"]);

        Assert.True(await GrantedAsync(player));
        Assert.False(await GrantedAsync(other));

        // Claimed again (another login before the answer was read): recorded once.
        await Ranked.ClaimRewardsAsync(player, "Season:SeasonFive", default);
        var stored = await Claims.Find(new BsonDocument("_id", ObjectId.Parse(player))).SingleAsync();
        Assert.Equal(new BsonArray { "Season:SeasonFive" }, stored["seasons"].AsBsonArray);
    }

    [SkippableFact]
    public async Task TheCurrentSeasonHasTheSameRatingsAsARunningSeason()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        // Season:Current is Season 6 by default.
        var seasonal = (await Ranked.DataAsync(new JsonObject { ["id"] = NewPlayer(), ["username"] = "p" }, default))["body"]!["SeasonalData"]!.AsObject();
        Assert.Equal(["Season:SeasonFive", "Season:SeasonSix"], seasonal.Select(kv => kv.Key));
        var five = seasonal["Season:SeasonFive"]!["Ranked"]!.AsObject();
        var six = seasonal["Season:SeasonSix"]!["Ranked"]!.AsObject();
        Assert.Equal(["DataByMode", "ClaimedRewards"], six.Select(kv => kv.Key));
        foreach (string mode in new[] { "1v1", "2v2" })
        {
            var expected = five["DataByMode"]![mode]!.DeepClone().AsObject();
            expected.Remove("FinalLeaderboardRank");
            Assert.Equal(expected.ToJsonString(), six["DataByMode"]![mode]!.ToJsonString());
        }
    }

    [SkippableFact]
    public async Task AnotherSeasonsClaimDoesNotGrantSeasonFive()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        string player = NewPlayer();
        await Ranked.ClaimRewardsAsync(player, "Season:SeasonSix", default);
        Assert.False(await GrantedAsync(player));
    }

    [SkippableTheory]
    [InlineData("")]
    [InlineData("5")]
    [InlineData("null")]
    [InlineData("{}")]
    public async Task AClaimWithoutASeasonNameRecordsNothing(string season)
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        string player = NewPlayer();
        var answer = await Ranked.ClaimRewardsAsync(player, season.Length == 0 ? JsonValue.Create("") : JsonNode.Parse(season), default);
        Assert.Equal(200, answer["return_code"]!.GetValue<int>());
        Assert.Equal(0, await Claims.CountDocumentsAsync(new BsonDocument("_id", ObjectId.Parse(player))));
    }

    [SkippableFact]
    public async Task AClaimFromASessionThatIsNotAPlayerRecordsNothing()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS and OVS_TEST_MONGO to run");
        await Ranked.ClaimRewardsAsync("not-a-player", "Season:SeasonFive", default);
        Assert.Equal(0, await Claims.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }
}

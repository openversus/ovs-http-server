using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OpenVersus.Server.Core.RewardTracks;
using OpenVersus.Server.Http.Shared.Hosting;
using StackExchange.Redis;

namespace OpenVersus.Server.Http.Tests;

/// <summary>
/// Claiming reward track tiers tells the game what it was paid (OnRewardsGranted), so the items unlock in its locker at once.
/// Without it a claimed battle pass item stayed locked until the next login (Tuggernuts, local stack, 2026-10-12: checked in
/// the pass, locked in the inventory, unlocked after a restart). Real Redis, database 15 (OVS_TEST_REDIS[_USER/_PW]).
/// </summary>
public sealed class ClaimEndpointTests(GameAppFactory factory) : IClassFixture<GameAppFactory>
{
    private static readonly string? s_redis = Environment.GetEnvironmentVariable("OVS_TEST_REDIS");
    private const string Track = "mrt_battlepass_season_five";

    private static readonly JsonObject Reward = new()
    {
        ["RewardGuid"] = "A59B6B59462CAB00DFD3542F8CA718F0",
        ["Constraints"] = new JsonArray(),
        ["RewardGrantMethod"] = "DirectInventoryItem",
        ["InventoryHsda"] = "skin_C027_s14",
        ["DirectInventoryItemCount"] = 1,
    };

    private sealed class Tracks : IRewardTrackService
    {
        private static JsonObject State() => new() { ["TrackSlug"] = Track, ["CurrentTier"] = 1 };

        public Task<JsonObject> AnswerAsync(string accountId, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<JsonObject>> AddScoreAsync(string accountId, IReadOnlyDictionary<string, int> points, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<(JsonObject? Track, IReadOnlyList<JsonObject> Claimed)> ClaimAllAsync(string accountId, string trackSlug, CancellationToken ct) =>
            Task.FromResult<(JsonObject?, IReadOnlyList<JsonObject>)>((State(), [(JsonObject)Reward.DeepClone()]));

        public Task<(JsonObject? Track, IReadOnlyList<JsonObject> Claimed)> ClaimAsync(string accountId, string trackSlug, IReadOnlyCollection<string>? tierGuids,
            CancellationToken ct) => ClaimAllAsync(accountId, trackSlug, ct);
    }

    private sealed class Grants : IRewardGrants
    {
        public Task<GrantResult> GrantAsync(string accountId, IReadOnlyList<JsonObject> rewards, IReadOnlyList<string> liveContainers, CancellationToken ct) =>
            Task.FromResult(new GrantResult(new JsonArray(rewards.Select(r => (JsonNode?)r.DeepClone()).ToArray()), []));

        public Task<IReadOnlyDictionary<string, long>> ItemsAsync(string accountId, CancellationToken ct) => throw new NotSupportedException();
    }

    [SkippableTheory]
    [InlineData("/ssc/invoke/claim_all_milestone_reward_track_tiers")]
    [InlineData("/ssc/invoke/claim_milestone_reward_track_tiers")]
    public async Task AClaimTellsTheGameTheItemsItWasPaid(string path)
    {
        Skip.If(string.IsNullOrEmpty(s_redis), "set OVS_TEST_REDIS to run");
        string[] parts = s_redis!.Split(':');
        var options = new ConfigurationOptions { EndPoints = { { parts[0], parts.Length > 1 ? int.Parse(parts[1]) : 6379 } }, DefaultDatabase = 15 };
        options.User = Environment.GetEnvironmentVariable("OVS_TEST_REDIS_USER");
        options.Password = Environment.GetEnvironmentVariable("OVS_TEST_REDIS_PW");
        await using var redis = await ConnectionMultiplexer.ConnectAsync(options);
        var heard = new ConcurrentQueue<JsonObject>();
        await redis.GetSubscriber().SubscribeAsync(RedisChannel.Literal("ws:send"), (_, message) =>
        {
            if (JsonNode.Parse(message.ToString()) is JsonObject sent && sent["playerIds"]!.ToJsonString().Contains(GameAppFactory.AccountId))
            {
                heard.Enqueue(sent);
            }
        });

        using var app = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.AddSingleton<IRewardTrackService>(new Tracks());
            s.AddSingleton<IRewardGrants>(new Grants());
            s.AddSingleton<IConnectionMultiplexer>(redis);
        }));
        var request = new HttpRequestMessage(HttpMethod.Put, path) { Content = new StringContent($$"""{"TrackSlug": "{{Track}}"}""") };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.Add(HydraToken.Header, GameAppFactory.Token());
        using var response = await app.CreateClient().SendAsync(request);
        response.EnsureSuccessStatusCode();

        JsonObject? granted = null;
        for (int i = 0; i < 50 && granted is null; i++)
        {
            granted = heard.Select(m => m["message"]?["data"] as JsonObject).FirstOrDefault(d => (string?)d?["template_id"] == "OnRewardsGranted");
            await Task.Delay(50);
        }

        Assert.NotNull(granted);
        Assert.Equal("RewardTrack", (string?)granted!["Context"]);
        Assert.Equal("skin_C027_s14", (string?)granted["RewardsGranted"]![0]!["InventoryHsda"]);
        await redis.GetDatabase().KeyDeleteAsync($"realtime:out:{GameAppFactory.AccountId}");
    }
}

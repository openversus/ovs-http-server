using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenVersus.Server.Core.Clients;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Cosmetics;
using OpenVersus.Server.Core.Identity;
using OpenVersus.Server.Core.Leaderboards;
using OpenVersus.Server.Core.Matches;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Tests.Matches;

/// <summary>
/// The matchmaking request and its cancel (tools/matches/queue_diff.mjs compares them with the TS server's). Real Redis,
/// database 15 (OVS_TEST_REDIS); no Mongo, so every skill is 0 (the ratings: EloRatings).
/// </summary>
[Collection(RedisTestDatabase.Name)]
public sealed class MatchmakingRequestTests : IAsyncLifetime
{
    private static readonly string? s_redis = Environment.GetEnvironmentVariable("OVS_TEST_REDIS");
    private const string Me = "0000000000000000000b0001";
    private const string Mate = "0000000000000000000b0002";
    private const string Lobby = "0000000000000000000b0100";
    private const string Ip = "198.51.100.31";

    private ConnectionMultiplexer? _redis;
    private readonly Gate _gate = new();
    private readonly ConcurrentQueue<(string Channel, string Message)> _published = new();

    private sealed class Gate : IClientUpdateGate
    {
        public HashSet<string> Outdated { get; } = [];
        public List<string> Modals { get; } = [];

        public Task<IReadOnlyList<ClientUpdateState>> RequiringUpdateAsync(IEnumerable<string> playerIds) =>
            Task.FromResult<IReadOnlyList<ClientUpdateState>>(playerIds.Where(Outdated.Contains).Select(p => new ClientUpdateState(p, "", false, true)).ToList());

        public Task<IReadOnlyList<bool>> RequestModalsAsync(IEnumerable<string> playerIds)
        {
            Modals.AddRange(playerIds);
            return Task.FromResult<IReadOnlyList<bool>>([]);
        }

        public Task<ClientUpdateState> ForRequestAsync(AccountLookup lookup, JsonObject? claims) => throw new NotSupportedException();
        public Task<double> ModalNonceAsync(string playerId) => throw new NotSupportedException();
        public JsonObject FailureBody() => new() { ["body"] = new JsonObject { ["error"] = "client_update_required" } };
    }

    private sealed class Cosmetics : ICosmeticsService
    {
        public List<string> Copied { get; } = [];
        public Task<JsonObject> EquippedAsync(string accountId, CancellationToken ct) => Task.FromResult(new JsonObject { ["Banner"] = "banner_x" });
        public Task WriteMatchCopyAsync(string accountId, JsonObject cosmetics)
        {
            Copied.Add(accountId);
            return Task.CompletedTask;
        }

        public Task<bool> EquipTauntAsync(string accountId, JsonNode? character, JsonNode? index, JsonNode? slug, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> EquipStatTrackerAsync(string accountId, JsonNode? index, JsonNode? slug, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> EquipAsync(CosmeticSlot slot, string accountId, JsonNode? slug, bool given, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> SetProfileIconAsync(string accountId, JsonNode? slug, CancellationToken ct) => throw new NotSupportedException();
    }

    private readonly Cosmetics _cosmetics = new();

    public async Task InitializeAsync()
    {
        if (string.IsNullOrEmpty(s_redis))
        {
            return;
        }

        string[] parts = s_redis.Split(':');
        var options = new ConfigurationOptions { EndPoints = { { parts[0], parts.Length > 1 ? int.Parse(parts[1]) : 6379 } }, DefaultDatabase = 15 };
        options.User = Environment.GetEnvironmentVariable("OVS_TEST_REDIS_USER");
        options.Password = Environment.GetEnvironmentVariable("OVS_TEST_REDIS_PW");
        _redis = await ConnectionMultiplexer.ConnectAsync(options);
        await CleanAsync();
        foreach (string channel in new[] { MatchmakingRequestService.QueuedChannel, PartyService.CancelMatchmakingChannel })
        {
            await _redis.GetSubscriber().SubscribeAsync(RedisChannel.Literal(channel), (c, m) => _published.Enqueue((c.ToString(), m.ToString())));
        }
    }

    public async Task DisposeAsync()
    {
        if (_redis is not null)
        {
            await CleanAsync();
            await _redis.DisposeAsync();
        }
    }

    private async Task CleanAsync()
    {
        var server = _redis!.GetServer(_redis.GetEndPoints()[0]);
        foreach (var key in server.Keys(15, "*0000000000000000000b*"))
        {
            await Db.KeyDeleteAsync(key);
        }

        await Db.KeyDeleteAsync(["1v1", "2v2", $"connections:{Ip}"]);
    }

    private IDatabase Db => _redis!.GetDatabase();

    private MatchmakingRequestService Service()
    {
        var services = new ServiceCollection().AddSingleton<IConnectionMultiplexer>(_redis!).BuildServiceProvider();
        return new MatchmakingRequestService(services, _gate, _cosmetics,
            new EloRatings(services, new TestOptions<RankedSettings>(new RankedSettings()), TimeProvider.System, NullLogger<EloRatings>.Instance),
            new TestOptions<LobbySettings>(new LobbySettings { GameVersion = "195303.1.1" }), TimeProvider.System, NullLogger<MatchmakingRequestService>.Instance);
    }

    private static PartyRequest Asking(string player) => new(player, new JsonObject { ["id"] = player, ["profile_id"] = "0000000000000000000b0900" }, Ip,
        new JsonObject { ["data"] = new JsonObject { ["MultiplayParams"] = new JsonObject { ["MultiplayClusterSlug"] = "ec2-us-east-1-dokken" } }, ["match"] = "0000000000000000000b0700" });

    private async Task PlayerAsync(string id, string character = "character_jason", bool ip = true)
    {
        HashEntry[] loadout = [new("character", character), new("skin", "skin_jason_default"), .. ip ? new HashEntry[] { new("ip", Ip) } : []];
        await Db.HashSetAsync($"player:{id}", loadout);
        await Db.HashSetAsync($"connections:{id}", [new("id", id), new("current_ip", Ip), new("character", "character_shaggy")]);
        await Db.StringSetAsync($"player:{id}:cosmetics", "{}");
    }

    private async Task LobbyAsync(params string[] players)
    {
        await Db.StringSetAsync($"lobby:{Lobby}", Js.Stringify(new JsonObject { ["lobbyId"] = Lobby, ["playerIds"] = new JsonArray([.. players.Select(p => (JsonNode?)p)]) }));
        foreach (string p in players)
        {
            await Db.StringSetAsync($"player_lobby:{p}", Lobby);
        }
    }

    private async Task<string> PublishedAsync(string channel)
    {
        for (int i = 0; i < 50; i++)
        {
            if (_published.FirstOrDefault(p => p.Channel == channel) is { Message: { } message })
            {
                return message;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException($"nothing on {channel}");
    }

    [SkippableFact]
    // queueMatch's ticket, key for key and in order (the matchmaker removes it by its bytes); no ip when player:{id} has none.
    public async Task TheTicketIsTheTsServersBytes()
    {
        Skip.If(_redis is null, "set OVS_TEST_REDIS to run");
        await PlayerAsync(Me);
        await PlayerAsync(Mate, ip: false);

        string ticket = await Service().TicketAsync(Db, Me, [Me, Mate], JsonValue.Create("0000000000000000000b0700"), "0000000000000000000b0800", "2v2",
            DateTimeOffset.FromUnixTimeSeconds(1790000000), CancellationToken.None);

        Assert.Equal(
            """{"created_at":1790000000,"matchType":"2v2","partyLeaderId":"0000000000000000000b0001","matchmakingRequestId":"0000000000000000000b0800","partyId":"0000000000000000000b0700","party_size":2,"players":[{"id":"0000000000000000000b0001","region":"MVSI","skill":0,"ip":"198.51.100.31"},{"id":"0000000000000000000b0002","region":"MVSI","skill":0}]}""",
            ticket);
    }

    [SkippableFact]
    public async Task ASoloRequestAnswersThenQueuesAndCleansUpFirst()
    {
        Skip.If(_redis is null, "set OVS_TEST_REDIS to run");
        await PlayerAsync(Me);
        await Db.ListRightPushAsync("1v1", """{"players":[{"id":"0000000000000000000b0001"}]}""");
        await Db.ListRightPushAsync("1v1", """{"players":[{"id":"someone_else"}]}""");
        await Db.StringSetAsync($"player_ranked_set:{Me}", "0000000000000000000b0200");
        await Db.StringSetAsync("ranked_set:0000000000000000000b0200", """{"players":[{"playerId":"0000000000000000000b0001"},{"playerId":"0000000000000000000b0003"}]}""");
        await Db.StringSetAsync("player_ranked_set:0000000000000000000b0003", "0000000000000000000b0200");

        var answer = (await Service().RequestAsync("ranked-1v1-retail", Asking(Me), CancellationToken.None))!;

        Assert.Equal(200, answer.Status);
        var body = answer.Body.AsObject();
        Assert.Equal("1v1-retail", (string?)body["criteria_slug"]);
        Assert.Equal("id", body.Last().Key);
        Assert.Equal("0000000000000000000b0900", (string?)body["players"]![Me]!["id"]);
        Assert.Null(body["party_id"]);
        // Nothing queued until the answer has been sent.
        Assert.Empty(_published);
        Assert.Equal(["""{"players":[{"id":"someone_else"}]}"""], (await Db.ListRangeAsync("1v1")).Select(v => v.ToString()));
        Assert.False(await Db.KeyExistsAsync("ranked_set:0000000000000000000b0200"));
        Assert.False(await Db.KeyExistsAsync("player_ranked_set:0000000000000000000b0003"));
        Assert.Equal("character_jason", (string?)await Db.HashGetAsync($"connections:{Me}", "character"));
        Assert.Empty(_cosmetics.Copied);

        await answer.After!();
        var ticket = Js.Parse(await PublishedAsync(MatchmakingRequestService.QueuedChannel))!;
        Assert.Equal("1v1", (string?)ticket["matchType"]);
        Assert.Equal((string?)body["id"], (string?)ticket["matchmakingRequestId"]);
        Assert.Equal("0000000000000000000b0700", (string?)ticket["partyId"]);
    }

    [SkippableFact]
    public async Task A1v1RequestFromALobbyOfTwoIsThe2v2Request()
    {
        Skip.If(_redis is null, "set OVS_TEST_REDIS to run");
        await PlayerAsync(Me);
        await PlayerAsync(Mate, character: "character_taz");
        await LobbyAsync(Me, Mate);

        var answer = (await Service().RequestAsync("1v1-retail", Asking(Me), CancellationToken.None))!;

        var body = answer.Body.AsObject();
        Assert.Equal("2v2-retail", (string?)body["criteria_slug"]);
        Assert.Equal("id", body.First().Key);
        Assert.Equal(Lobby, (string?)body["party_id"]);
        Assert.Equal(2, (int)body["data"]!["player_count"]!);
        // The teammate's loadout becomes their session's too.
        Assert.Equal("character_taz", (string?)await Db.HashGetAsync($"connections:{Mate}", "character"));
        await answer.After!();
        var ticket = Js.Parse(await PublishedAsync(MatchmakingRequestService.QueuedChannel))!;
        Assert.Equal([Me, Mate], ticket["players"]!.AsArray().Select(p => (string?)p!["id"]));
    }

    [SkippableFact]
    public async Task A2v2WithADisconnectedTeammateIsRefused()
    {
        Skip.If(_redis is null, "set OVS_TEST_REDIS to run");
        await PlayerAsync(Me);
        await LobbyAsync(Me, Mate);

        var answer = (await Service().RequestAsync("2v2-retail", Asking(Me), CancellationToken.None))!;

        Assert.Equal((200, """{"error":"Not all party members are connected"}"""), (answer.Status, Js.Stringify(answer.Body)));
        Assert.Null(answer.After);
    }

    [SkippableFact]
    public async Task NoLockedLoadoutIs500AndAPlayerWhoNeverEquippedGetsTheirCosmeticsCached()
    {
        Skip.If(_redis is null, "set OVS_TEST_REDIS to run");
        await Db.HashSetAsync($"connections:{Me}", [new("id", Me)]);

        var answer = (await Service().RequestAsync("1v1-retail", Asking(Me), CancellationToken.None))!;

        Assert.Equal((500, """{"error":"player_loadout_not_found"}"""), (answer.Status, Js.Stringify(answer.Body)));
        Assert.Equal([Me], _cosmetics.Copied);
    }

    [SkippableFact]
    public async Task AnOutdatedTeammateBlocksThe2v2WithTheModal()
    {
        Skip.If(_redis is null, "set OVS_TEST_REDIS to run");
        await PlayerAsync(Me);
        await PlayerAsync(Mate);
        await LobbyAsync(Me, Mate);
        _gate.Outdated.Add(Mate);

        var answer = (await Service().RequestAsync("2v2-retail", Asking(Me), CancellationToken.None))!;

        Assert.Equal("client_update_required", (string?)answer.Body["body"]!["error"]);
        Assert.Equal([Mate], _gate.Modals);
    }

    [SkippableFact]
    public async Task AnOtherCriteriaIsNotAnswered()
    {
        Skip.If(_redis is null, "set OVS_TEST_REDIS to run");
        Assert.Null(await Service().RequestAsync("ranked-2v2-retail", Asking(Me), CancellationToken.None));
    }

    [SkippableFact]
    public async Task ACancelReachesTheWholeLobbyAndUnreadiesIt()
    {
        Skip.If(_redis is null, "set OVS_TEST_REDIS to run");
        await LobbyAsync(Me, Mate);
        await Db.StringSetAsync($"party_ready:{Lobby}", "1");

        var answer = await Service().CancelAsync("0000000000000000000b0800", Asking(Me), CancellationToken.None);

        Assert.Equal("""{"body":{},"metadata":null,"return_code":0}""", Js.Stringify(answer));
        Assert.Equal("""{"playersIds":["0000000000000000000b0001","0000000000000000000b0002"],"matchmakingId":"0000000000000000000b0800"}""",
            await PublishedAsync(PartyService.CancelMatchmakingChannel));
        Assert.False(await Db.KeyExistsAsync($"party_ready:{Lobby}"));
    }
}

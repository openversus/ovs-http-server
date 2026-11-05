using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Matches;
using OpenVersus.Server.Core.Realtime;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Tests.Matches;

/// <summary>
/// A party's place in a queue (<see cref="MatchmakingQueue"/>): who is told and given the tick,
/// what a cancel and a found match take away. Parity with the TS websocket is tools/matches/queue_diff.mjs. Real Redis
/// (database 15, OVS_TEST_REDIS).
/// </summary>
[Collection(RedisTestDatabase.Name)]
public sealed class MatchmakingQueueTests : IAsyncLifetime
{
    private static readonly string? s_redis = Environment.GetEnvironmentVariable("OVS_TEST_REDIS");
    private static string Id(int n) => $"0000000000000000001a{n:D4}";
    private static readonly string P1 = Id(1), P2 = Id(2), P3 = Id(3);
    private const string List = "2v2";

    private ConnectionMultiplexer? _redis;
    private readonly ConcurrentQueue<JsonObject> _sent = new();

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
        await _redis.GetSubscriber().SubscribeAsync(RedisChannel.Literal(ProfileNotifications.WsSendChannel), (_, m) =>
        {
            if (m.ToString().Contains("0000000000000000001a", StringComparison.Ordinal) && Js.Parse(m.ToString()) is JsonObject send)
            {
                _sent.Enqueue(send);
            }
        });
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
        foreach (var key in server.Keys(15, "*0000000000000000001a*"))
        {
            await Db.KeyDeleteAsync(key);
        }

        foreach (string list in new[] { "1v1", "2v2", "casual1v1", "casual2v2" })
        {
            foreach (var raw in await Db.ListRangeAsync(list))
            {
                if (raw.ToString().Contains("0000000000000000001a", StringComparison.Ordinal))
                {
                    await Db.ListRemoveAsync(list, raw);
                }
            }
        }

        foreach (string player in new[] { P1, P2, P3 })
        {
            await Db.HashDeleteAsync(MatchmakingQueue.QueuedKey, player);
        }
    }

    private IDatabase Db => _redis!.GetDatabase();

    private static string Ticket(string list, string request, params string[] players) => Js.Stringify(new JsonObject
    {
        ["created_at"] = 1790000000,
        ["matchType"] = list,
        ["partyLeaderId"] = players[0],
        ["matchmakingRequestId"] = request,
        ["partyId"] = Id(700),
        ["party_size"] = players.Length,
        ["players"] = new JsonArray([.. players.Select(p => (JsonNode)new JsonObject { ["id"] = p, ["region"] = "MVSI", ["skill"] = 0 })]),
    });

    private async Task<List<(string Player, JsonObject Message)>> SentAsync()
    {
        await Task.Delay(200);
        return [.. _sent.Select(s => ((string)s["playerIds"]![0]!, (JsonObject)s["message"]!))];
    }

    private async Task<string?> StatusAsync(string player) => (string?)await Db.HashGetAsync($"player:{player}", "status");

    [SkippableFact]
    public async Task OnlyAConnectedPlayerIsToldAndTickedButTheTicketIsQueuedForAll()
    {
        Skip.If(_redis is null, "OVS_TEST_REDIS not set");
        // A ticket of P2's already on the list (an earlier request) goes; P3's (not in this party) stays.
        string old = Ticket(List, "old", P2, Id(9));
        string other = Ticket(List, "other", P3);
        await Db.ListRightPushAsync(List, [old, other]);
        await Db.HashSetAsync(GatewayPresence.ConnectionKey(P1), "id", "c1");
        string ticket = Ticket(List, "req-1", P1, P2);

        await MatchmakingQueue.QueueAsync(Db, ticket);

        Assert.Equal([other, ticket], (await Db.ListRangeAsync(List)).Select(v => v.ToString()));
        var (player, message) = Assert.Single(await SentAsync());
        Assert.Equal(P1, player);
        Assert.Equal($$"""{"data":{"template_id":"OnMatchmakerStarted","MatchmakingRequestId":"req-1"},"payload":{"match":{"id":"{{Id(700)}}"},"custom_notification":"realtime"},"header":"","cmd":"update"}""",
            Js.Stringify(message));
        Assert.Equal(("queued", ticket), (await StatusAsync(P1), (string?)await Db.HashGetAsync(MatchmakingQueue.QueuedKey, P1)));
        Assert.Equal((null, false), (await StatusAsync(P2), await Db.HashExistsAsync(MatchmakingQueue.QueuedKey, P2)));
    }

    [SkippableFact]
    public async Task ACancelTakesEachHeldTicketFromItsOwnListAndTellsItsHolder()
    {
        Skip.If(_redis is null, "OVS_TEST_REDIS not set");
        // A Casual ticket (TS took cancelled tickets out of 1v1 and 2v2 only); P2 holds none.
        string ticket = Ticket("casual1v1", "req-2", P1);
        await Db.ListRightPushAsync("casual1v1", ticket);
        await Db.HashSetAsync(MatchmakingQueue.QueuedKey, P1, ticket);
        await Db.HashSetAsync($"player:{P2}", "status", "in_match");

        await MatchmakingQueue.CancelAsync(Db, [P1, P2], "party-changed");

        Assert.Empty(await Db.ListRangeAsync("casual1v1"));
        Assert.False(await Db.HashExistsAsync(MatchmakingQueue.QueuedKey, P1));
        var (player, message) = Assert.Single(await SentAsync());
        Assert.Equal((P1, """{"data":{},"payload":{"id":"party-changed","state":3},"header":"Matchmaking request cancelled.","cmd":"matchmaking-cancel"}"""),
            (player, Js.Stringify(message)));
        Assert.Equal(("idle", "in_match"), (await StatusAsync(P1), await StatusAsync(P2)));
    }

    [SkippableFact]
    public async Task AFoundMatchStopsTheTicksAndSetsInMatchOnlyWhereOneRan()
    {
        Skip.If(_redis is null, "OVS_TEST_REDIS not set");
        await Db.HashSetAsync(MatchmakingQueue.QueuedKey, P1, Ticket(List, "req-3", P1));
        await Db.HashSetAsync($"player:{P2}", "status", "idle");

        await MatchmakingQueue.FoundAsync(Db, [P1, P2]);

        Assert.False(await Db.HashExistsAsync(MatchmakingQueue.QueuedKey, P1));
        Assert.Equal(("in_match", "idle"), (await StatusAsync(P1), await StatusAsync(P2)));
        Assert.Empty(await SentAsync());
    }

    [SkippableFact]
    // A party member's game is gone: the ticket leaves its list for good; the member who is still there is told (the
    // ticket's request, as their own cancel) instead of being left searching for a ticket that is gone.
    public async Task ADroppedTicketLeavesItsListAndTheRestOfThePartyIsCancelledAndTold()
    {
        Skip.If(_redis is null, "OVS_TEST_REDIS not set");
        string ticket = Ticket(List, "req-4", P1, P2);
        await Db.ListRightPushAsync(List, ticket);
        await Db.HashSetAsync(MatchmakingQueue.QueuedKey, [new(P1, ticket), new(P2, ticket)]);

        Assert.True(await MatchmakingQueue.DropAsync(Db, P1, 1790000001000));

        Assert.Empty(await Db.ListRangeAsync(List));
        Assert.False(await Db.HashExistsAsync(MatchmakingQueue.QueuedKey, P1));
        Assert.False(await Db.HashExistsAsync(MatchmakingQueue.QueuedKey, P2));
        var (player, message) = Assert.Single(await SentAsync());
        Assert.Equal((P2, """{"data":{},"payload":{"id":"req-4","state":3},"header":"Matchmaking request cancelled.","cmd":"matchmaking-cancel"}"""),
            (player, Js.Stringify(message)));
        Assert.Equal(("idle", "idle"), (await StatusAsync(P1), await StatusAsync(P2)));
    }

    [SkippableFact]
    // A ticket queued after the game went (the new game's, after a quick login) is not the one to drop.
    public async Task ATicketQueuedAfterTheGameWentIsKept()
    {
        Skip.If(_redis is null, "OVS_TEST_REDIS not set");
        string ticket = Ticket(List, "req-5", P1);
        await Db.ListRightPushAsync(List, ticket);
        await Db.HashSetAsync(MatchmakingQueue.QueuedKey, P1, ticket);

        Assert.False(await MatchmakingQueue.DropAsync(Db, P1, 1789999999000));

        Assert.Equal([ticket], (await Db.ListRangeAsync(List)).Select(v => v.ToString()));
        Assert.Equal(ticket, (string?)await Db.HashGetAsync(MatchmakingQueue.QueuedKey, P1));
        Assert.Empty(await SentAsync());
    }

    [Fact]
    public void TheTickNamesTheTicketsRequest()
    {
        Assert.Equal("""{"data":{},"payload":{"id":"req-4","state":2},"header":"matchmaking-tick","cmd":"matchmaking-tick"}""",
            Js.Stringify(MatchmakingQueue.Tick(MatchmakingQueue.RequestIdOf(Ticket(List, "req-4", P1)))));
    }
}

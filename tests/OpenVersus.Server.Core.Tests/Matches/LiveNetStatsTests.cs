using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Matches;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Tests.Matches;

/// <summary>
/// A match's live network figures (<see cref="LiveNetStats"/>): kept per player from the reports of the match's own
/// players, shown while fresh, with how the match is played. Against a real Redis (database 15, OVS_TEST_REDIS).
/// </summary>
[Collection(RedisTestDatabase.Name)]
public sealed class LiveNetStatsTests : IAsyncLifetime
{
    private static readonly string? s_redis = Environment.GetEnvironmentVariable("OVS_TEST_REDIS");
    private const string Match = "0000000000000000002f0f01", P1 = "0000000000000000002f0001", P2 = "0000000000000000002f0002", Watcher = "0000000000000000002f0003",
        Bot = "Bot0000000000000000002f0000000000001", Stranger = "0000000000000000002f0009";
    private static readonly DateTimeOffset s_now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    private ConnectionMultiplexer? _redis;

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
    }

    public async Task DisposeAsync()
    {
        if (_redis is not null)
        {
            await CleanAsync();
            await _redis.DisposeAsync();
        }
    }

    private IDatabase Db => _redis!.GetDatabase();

    private Task CleanAsync() => Db.KeyDeleteAsync([Match, LiveNetStats.Key(Match), $"p2p_relay:{Match}", LiveNetStats.OnRelayKey(Match)]);

    // The match's config, as its launch keeps it.
    private Task ConfigAsync(bool p2p, bool custom = false, string? eventQueue = null) => Db.StringSetAsync(Match, new JsonObject
    {
        ["matchId"] = Match, ["mode"] = "1v1", ["p2p"] = p2p, ["isCustomGame"] = custom,
        ["gameplayConfigOverride"] = eventQueue is null ? null : new JsonObject { ["EventQueueSlug"] = eventQueue },
        ["players"] = new JsonArray(
            new JsonObject { ["playerId"] = P1, ["teamIndex"] = 0, ["isBot"] = false },
            new JsonObject { ["playerId"] = P2, ["teamIndex"] = 1, ["isBot"] = false },
            new JsonObject { ["playerId"] = Bot, ["teamIndex"] = 1, ["isBot"] = true },
            new JsonObject { ["playerId"] = Watcher, ["teamIndex"] = 4, ["isSpectator"] = true }),
    }.ToJsonString());

    private static JsonObject Report(object? match = null, double delay = 3, double ping = 42, double depth = 1.75, bool final = false)
    {
        var report = new JsonObject { ["matchId"] = JsonValue.Create(match ?? Match), ["delay"] = delay, ["ping"] = ping, ["depth"] = depth };
        if (final)
        {
            report["final"] = true;
        }

        return report;
    }

    [SkippableFact]
    public async Task APlayersReportIsKeptAndShownWhileFresh()
    {
        Skip.If(_redis is null, "OVS_TEST_REDIS not set");
        await ConfigAsync(p2p: true);

        Assert.Equal(NetStatsVerdict.WasKept, await LiveNetStats.ReportAsync(Db, P1, Report(), s_now));
        Assert.Equal(NetStatsVerdict.WasKept, await LiveNetStats.ReportAsync(Db, P2, Report(delay: 5, ping: 120, depth: 0), s_now.AddSeconds(10)));

        // Both while fresh; a report two intervals old and more is no longer shown (its player stopped reporting).
        var stats = await LiveNetStats.ReadAsync(Db, Match, s_now.AddSeconds(12));
        Assert.Equal(new LiveNetStat(3, 42, 1.75), stats[P1]);
        Assert.Equal(new LiveNetStat(5, 120, 0), stats[P2]);
        Assert.Equal([P2], (await LiveNetStats.ReadAsync(Db, Match, s_now.AddSeconds(30))).Keys);
        Assert.Empty(await LiveNetStats.ReadAsync(Db, Match, s_now.AddSeconds(60)));
        // The match's figures go with it.
        Assert.InRange((await Db.KeyTimeToLiveAsync(LiveNetStats.Key(Match)))!.Value, TimeSpan.FromSeconds(55), LiveNetStats.Kept);

        // A newer report replaces the player's, and what a game cannot show is bounded.
        Assert.Equal(NetStatsVerdict.WasKept, await LiveNetStats.ReportAsync(Db, P1, Report(delay: 9000, ping: -5, depth: 12.3456), s_now.AddSeconds(15)));
        Assert.Equal(new LiveNetStat(255, 0, 12.35), (await LiveNetStats.ReadAsync(Db, Match, s_now.AddSeconds(16)))[P1]);
    }

    [SkippableFact]
    // The last report of a match, marked final, is what stays on it: shown however old it is, kept as long as the
    // finished match is listed, and not replaced by a report from before the end that arrives after it.
    public async Task TheFinalReportStaysOnTheMatch()
    {
        Skip.If(_redis is null, "OVS_TEST_REDIS not set");
        await ConfigAsync(p2p: true);
        Assert.Equal(NetStatsVerdict.WasKept, await LiveNetStats.ReportAsync(Db, P1, Report(), s_now));
        Assert.Equal(NetStatsVerdict.WasKept, await LiveNetStats.ReportAsync(Db, P2, Report(delay: 5, ping: 120, depth: 0.5), s_now));

        Assert.Equal(NetStatsVerdict.WasKept, await LiveNetStats.ReportAsync(Db, P1, Report(delay: 4, ping: 50, depth: 2.25, final: true), s_now.AddSeconds(60)));
        Assert.InRange((await Db.KeyTimeToLiveAsync(LiveNetStats.Key(Match)))!.Value, LiveNetStats.FinalKept - TimeSpan.FromSeconds(5), LiveNetStats.FinalKept);
        // A straggler from before the end is taken and changes nothing; nor does it shorten the match's figures' life.
        Assert.Equal(NetStatsVerdict.WasKept, await LiveNetStats.ReportAsync(Db, P1, Report(delay: 9, ping: 9, depth: 9), s_now.AddSeconds(61)));
        Assert.Equal(NetStatsVerdict.WasKept, await LiveNetStats.ReportAsync(Db, P2, Report(delay: 5, ping: 121, depth: 0.5), s_now.AddSeconds(61)));
        Assert.InRange((await Db.KeyTimeToLiveAsync(LiveNetStats.Key(Match)))!.Value, LiveNetStats.FinalKept - TimeSpan.FromSeconds(5), LiveNetStats.FinalKept);

        // Minutes later: the final one is still shown, the other player's last live one has aged out.
        var later = await LiveNetStats.ReadAsync(Db, Match, s_now.AddMinutes(4));
        Assert.Equal(new LiveNetStat(4, 50, 2.25, Final: true), Assert.Single(later).Value);
        Assert.Equal(P1, later.Keys.Single());
    }

    [SkippableFact]
    // A match the page does not list (a custom lobby's, a Casual one: marked isCustomGame) has its reports taken and
    // nothing done with them; a Testing Grounds match is marked the same way and is listed, so its reports are kept.
    public async Task AReportForAMatchThePageDoesNotListIsTakenAndNotKept()
    {
        Skip.If(_redis is null, "OVS_TEST_REDIS not set");
        await ConfigAsync(p2p: true, custom: true);
        Assert.Equal(NetStatsVerdict.NotListed, await LiveNetStats.ReportAsync(Db, P1, Report(), s_now));
        Assert.Equal(NetStatsVerdict.NotListed, await LiveNetStats.ReportAsync(Db, P1, Report(final: true), s_now));
        Assert.False(await Db.KeyExistsAsync(LiveNetStats.Key(Match)));
        // Still only from its own players.
        Assert.Equal(NetStatsVerdict.Refuse("not a player of that match"), await LiveNetStats.ReportAsync(Db, Stranger, Report(), s_now));

        await ConfigAsync(p2p: true, custom: true, eventQueue: OpenVersus.Server.Core.Matchmaking.TestingGrounds.OneVsOne.EventQueue);
        Assert.Equal(NetStatsVerdict.WasKept, await LiveNetStats.ReportAsync(Db, P1, Report(), s_now));
    }

    [SkippableFact]
    public async Task OnlyAPlayerOfTheMatchMayReport()
    {
        Skip.If(_redis is null, "OVS_TEST_REDIS not set");
        await ConfigAsync(p2p: true);

        Assert.Equal(NetStatsVerdict.Refuse("not a player of that match"), await LiveNetStats.ReportAsync(Db, Stranger, Report(), s_now));
        Assert.Equal(NetStatsVerdict.Refuse("not a player of that match"), await LiveNetStats.ReportAsync(Db, Watcher, Report(), s_now));
        Assert.Equal(NetStatsVerdict.Refuse("not a player of that match"), await LiveNetStats.ReportAsync(Db, Bot, Report(), s_now));
        Assert.Equal(NetStatsVerdict.Refuse("not a player of that match"), await LiveNetStats.ReportAsync(Db, P1, Report(match: "0000000000000000002f0f99"), s_now));
        Assert.Equal(NetStatsVerdict.Refuse("no match id"), await LiveNetStats.ReportAsync(Db, P1, Report(match: "p2p_relay:x"), s_now));
        Assert.Equal(NetStatsVerdict.Refuse("no match id"), await LiveNetStats.ReportAsync(Db, P1, Report(match: 7), s_now));
        Assert.Equal(NetStatsVerdict.Refuse("no match id"), await LiveNetStats.ReportAsync(Db, P1, null, s_now));
        var words = Report();
        words["ping"] = "fast";
        Assert.Equal(NetStatsVerdict.Refuse("delay, ping and depth must be numbers"), await LiveNetStats.ReportAsync(Db, P1, words, s_now));
        Assert.False(await Db.KeyExistsAsync(LiveNetStats.Key(Match)));
    }

    [SkippableFact]
    public async Task TheTransportIsP2PUntilTheRelayIsAskedFor()
    {
        Skip.If(_redis is null, "OVS_TEST_REDIS not set");
        Assert.Null(await LiveNetStats.TransportAsync(Db, Match));

        await ConfigAsync(p2p: false);
        Assert.Equal(LiveNetStats.Relay, await LiveNetStats.TransportAsync(Db, Match));

        await ConfigAsync(p2p: true);
        Assert.Equal(LiveNetStats.P2P, await LiveNetStats.TransportAsync(Db, Match));
        // A node reported no direct path and was given the relay (RollbackCallbacks: /ovs_p2p_failed).
        await Db.StringSetAsync($"p2p_relay:{Match}", "1");
        Assert.Equal(LiveNetStats.RelayAfterP2P, await LiveNetStats.TransportAsync(Db, Match));
        // Or a node took the relay without asking, and the relay's own events are the only sign (MatchStatusEvents).
        await Db.KeyDeleteAsync($"p2p_relay:{Match}");
        Assert.Equal(LiveNetStats.P2P, await LiveNetStats.TransportAsync(Db, Match));
        await Db.StringSetAsync(LiveNetStats.OnRelayKey(Match), "1");
        Assert.Equal(LiveNetStats.RelayAfterP2P, await LiveNetStats.TransportAsync(Db, Match));
    }
}

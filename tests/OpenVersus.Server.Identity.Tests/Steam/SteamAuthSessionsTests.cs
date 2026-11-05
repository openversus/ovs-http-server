using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenVersus.Server.Core.Hosting;
using OpenVersus.Server.Core.Realtime;
using OpenVersus.Server.Core.Steam;
using OpenVersus.Server.Identity.Steam;
using StackExchange.Redis;

namespace OpenVersus.Server.Identity.Tests.Steam;

/// <summary>
/// The session state machine driven by hand (no loop) over a fake Steam, against a real Redis (database 1,
/// OVS_TEST_REDIS): what each event writes where the other services read it.
/// </summary>
public sealed class SteamAuthSessionsTests : IAsyncLifetime
{
    private const int TestRedisDb = 1;
    private const string SteamId = "76561198000000091";
    private const ulong SteamIdValue = 76561198000000091;
    private const string Player = "0000000000000000000a0091";
    private const string Ip = "198.51.100.90";
    private static readonly string? s_redis = Environment.GetEnvironmentVariable("OVS_TEST_REDIS");
    private readonly FakeSteam _steam = new();
    private readonly FakeTime _time = new(new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero));
    private readonly SteamSettings _settings = new() { Enabled = true, VerdictTimeoutMs = 5000 };
    private ConnectionMultiplexer? _redis;
    private SteamAuthSessions? _sessions;

    private static bool Configured => !string.IsNullOrEmpty(s_redis);

    private IDatabase Redis => _redis!.GetDatabase();

    private SteamAuthSessions Sessions => _sessions ??= new SteamAuthSessions(
        new ServiceCollection().AddSingleton<IConnectionMultiplexer>(_redis!).BuildServiceProvider(),
        _steam, new ServiceInstance(), new Options(_settings), _time, NullLogger<SteamAuthSessions>.Instance);

    public async Task InitializeAsync()
    {
        if (!Configured)
        {
            return;
        }

        string[] parts = s_redis!.Split(':');
        _redis = await ConnectionMultiplexer.ConnectAsync(new ConfigurationOptions
        {
            EndPoints = { { parts[0], parts.Length > 1 ? int.Parse(parts[1]) : 6379 } },
            User = Environment.GetEnvironmentVariable("OVS_TEST_REDIS_USER"),
            Password = Environment.GetEnvironmentVariable("OVS_TEST_REDIS_PW"),
            DefaultDatabase = TestRedisDb,
        });
        await ClearAsync();
    }

    public async Task DisposeAsync()
    {
        if (_redis is not null)
        {
            await ClearAsync();
            _redis.Dispose();
        }
    }

    [SkippableFact]
    public async Task AnOpenAsksSteamAndOkHoldsTheSessionWithPresence()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS to run");
        // As /api/identify queues it on a first launch: no account yet; the login's index names one by the verdict.
        var request = Request("h1", player: "");

        await Sessions.OpenAsync(request);

        Assert.Equal([SteamIdValue], _steam.Held.Keys);
        var pending = await SteamSessions.ReadAsync(Redis, SteamId);
        Assert.Equal((SteamSessions.Pending, "h1", "", Ip), (pending!.State, pending.TicketHash, pending.PlayerId, (string?)await Redis.HashGetAsync(SteamSessions.SessionKey(SteamId), "ip")));
        await Redis.StringSetAsync($"identity:steam:{SteamId}", Player);
        Assert.InRange((await Redis.KeyTimeToLiveAsync(SteamSessions.SessionKey(SteamId)))!.Value, TimeSpan.FromHours(23), TimeSpan.FromHours(24));

        await Sessions.VerdictAsync(_steam.Ok(SteamIdValue, owner: 76561198000000001));

        var ok = await SteamSessions.ReadAsync(Redis, SteamId);
        Assert.Equal((SteamSessions.Ok, "OK", "76561198000000001"), (ok!.State, ok.Response, ok.OwnerSteamId));
        Assert.Equal(Player, (string?)await Redis.HashGetAsync(SteamSessions.OnlineKey, SteamId));
        var status = await Sessions.StatusAsync();
        Assert.Equal((1, 1L), (status.Sessions[SteamSessions.Ok], status.Verdicts["ok"]));
    }

    [SkippableFact]
    public async Task TheSameTicketAgainIsNotOpenedTwice()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS to run");
        await Sessions.OpenAsync(Request("h1", player: ""));
        await Sessions.VerdictAsync(_steam.Ok(SteamIdValue));

        // The client retries with the same ticket (and knows its account now).
        await Sessions.OpenAsync(Request("h1"));

        Assert.Equal(1, _steam.Opens);
        var session = await SteamSessions.ReadAsync(Redis, SteamId);
        Assert.Equal((SteamSessions.Ok, Player), (session!.State, session.PlayerId));
    }

    [SkippableFact]
    public async Task ANewTicketForTheSameSteamIdReplacesTheOldSession()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS to run");
        await Sessions.OpenAsync(Request("h1"));
        await Sessions.VerdictAsync(_steam.Ok(SteamIdValue));
        uint first = _steam.Held[SteamIdValue];

        await Sessions.OpenAsync(Request("h2"));

        Assert.Equal([SteamIdValue], _steam.Ended);
        Assert.NotEqual(first, _steam.Held[SteamIdValue]);
        var session = await SteamSessions.ReadAsync(Redis, SteamId);
        Assert.Equal((SteamSessions.Pending, "h2"), (session!.State, session.TicketHash));
        // A verdict for the old ticket's crc is not the new session's.
        await Sessions.VerdictAsync(new SteamVerdict(SteamIdValue, SteamIdValue, first, "AuthTicketCanceled", false));
        Assert.Equal(SteamSessions.Pending, (await SteamSessions.ReadAsync(Redis, SteamId))!.State);
    }

    [SkippableFact]
    public async Task ARefusalDisconnectsThePlayerAndHoldsTheSteamId()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS to run");
        var disconnect = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _redis!.GetSubscriber().SubscribeAsync(RedisChannel.Literal(GatewayChannels.Disconnect), (_, message) => disconnect.TrySetResult(message.ToString()));
        await Sessions.OpenAsync(Request("h1"));

        await Sessions.VerdictAsync(new SteamVerdict(SteamIdValue, SteamIdValue, _steam.Held[SteamIdValue], "NoLicenseOrExpired", false));

        var session = await SteamSessions.ReadAsync(Redis, SteamId);
        Assert.Equal((SteamSessions.Refused, "NoLicenseOrExpired"), (session!.State, session.Response));
        Assert.True(await SteamSessions.RefusedRecentlyAsync(Redis, SteamId, TimeSpan.FromMinutes(10), _time.GetUtcNow()));
        Assert.False(await SteamSessions.RefusedRecentlyAsync(Redis, SteamId, TimeSpan.FromMinutes(10), _time.GetUtcNow().AddMinutes(11)));
        Assert.False(await Redis.HashExistsAsync(SteamSessions.OnlineKey, SteamId));
        Assert.Empty(_steam.Held);
        Assert.Equal(Player, (string?)JsonNode.Parse(await disconnect.Task.WaitAsync(TimeSpan.FromSeconds(5)))!["playerId"]);
    }

    [SkippableFact]
    public async Task AlreadyUsedOnATicketThisConnectionOpenedIsNoVerdict()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS to run");
        await Sessions.OpenAsync(Request("h1"));

        await Sessions.VerdictAsync(new SteamVerdict(SteamIdValue, 0, _steam.Held[SteamIdValue], SteamVerdict.AlreadyUsed, false));

        var session = await SteamSessions.ReadAsync(Redis, SteamId);
        Assert.Equal(SteamSessions.Unavailable, session!.State);
        Assert.False(await SteamSessions.RefusedRecentlyAsync(Redis, SteamId, TimeSpan.FromMinutes(10), _time.GetUtcNow()));
        Assert.Empty(_steam.Held);
    }

    [SkippableFact]
    public async Task ARepeatedOkKeepsTheSessionHeld()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS to run");
        await Sessions.OpenAsync(Request("h1"));
        await Sessions.VerdictAsync(_steam.Ok(SteamIdValue));

        // Another player's open re-sends the list; Steam judges this entry again.
        await Sessions.VerdictAsync(_steam.Ok(SteamIdValue));

        var session = await SteamSessions.ReadAsync(Redis, SteamId);
        Assert.Equal(SteamSessions.Ok, session!.State);
        Assert.Equal(Player, (string?)await Redis.HashGetAsync(SteamSessions.OnlineKey, SteamId));
        Assert.Equal([SteamIdValue], _steam.Held.Keys);
        Assert.Equal((1, 1L), ((await Sessions.StatusAsync()).Sessions[SteamSessions.Ok], (await Sessions.StatusAsync()).Verdicts["ok"]));
    }

    [SkippableFact]
    public async Task AVerdictAfterOkEndsPresenceAndNothingMore()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS to run");
        await Sessions.OpenAsync(Request("h1"));
        await Sessions.VerdictAsync(_steam.Ok(SteamIdValue));
        uint crc = _steam.Held[SteamIdValue];
        _time.Advance(TimeSpan.FromMinutes(30));

        await Sessions.VerdictAsync(new SteamVerdict(SteamIdValue, SteamIdValue, crc, SteamVerdict.Canceled, false));

        var session = await SteamSessions.ReadAsync(Redis, SteamId);
        Assert.Equal((SteamSessions.Canceled, SteamVerdict.Canceled), (session!.State, session.Response));
        Assert.False(await Redis.HashExistsAsync(SteamSessions.OnlineKey, SteamId));
        Assert.False(await SteamSessions.RefusedRecentlyAsync(Redis, SteamId, TimeSpan.FromMinutes(10), _time.GetUtcNow()));
        Assert.Empty(_steam.Held);
        Assert.Equal(0, (await Sessions.StatusAsync()).Sessions.Count);
    }

    [SkippableFact]
    public async Task LosingSteamMarksEverySessionUnavailableAndAsksForANewTicketOnceBack()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS to run");
        await Sessions.OpenAsync(Request("h1"));
        await Sessions.VerdictAsync(_steam.Ok(SteamIdValue));
        await Sessions.ConnectedAsync();

        _steam.IsConnected = false;
        await Sessions.DisconnectedAsync("connection to Steam lost");

        var session = await SteamSessions.ReadAsync(Redis, SteamId);
        Assert.Equal(SteamSessions.Unavailable, session!.State);
        Assert.False(await Redis.HashExistsAsync(SteamSessions.OnlineKey, SteamId));
        Assert.False(await SteamSessions.ConnectedAsync(Redis));
        Assert.Empty(await Redis.ListRangeAsync(PlayerMessages.NotificationPrefix + Player));

        _steam.IsConnected = true;
        await Sessions.ConnectedAsync();

        Assert.True(await SteamSessions.ConnectedAsync(Redis));
        var queued = await Redis.ListRangeAsync(PlayerMessages.NotificationPrefix + Player);
        var notification = JsonNode.Parse(Assert.Single(queued).ToString())!;
        Assert.Equal((SteamAuthSessions.ReidentifyNotification, SteamId), ((string?)notification["type"], (string?)notification["data"]!["steamId"]));
        Assert.Equal(0, (await Sessions.StatusAsync()).Sessions.Count);
    }

    [SkippableFact]
    public async Task NoVerdictInTimeIsUnavailable()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS to run");
        await Sessions.OpenAsync(Request("h1"));
        _time.Advance(TimeSpan.FromSeconds(4));
        await Sessions.TickAsync();
        Assert.Equal(SteamSessions.Pending, (await SteamSessions.ReadAsync(Redis, SteamId))!.State);

        _time.Advance(TimeSpan.FromSeconds(2));
        await Sessions.TickAsync();

        var session = await SteamSessions.ReadAsync(Redis, SteamId);
        Assert.Equal((SteamSessions.Unavailable, "no verdict"), (session!.State, session.Response));
        Assert.Empty(_steam.Held);
        Assert.Equal(1L, (await Sessions.StatusAsync()).Verdicts["unavailable"]);
    }

    [SkippableFact]
    public async Task AVerdictForATicketNotHeldIsIgnored()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS to run");

        await Sessions.VerdictAsync(new SteamVerdict(SteamIdValue, SteamIdValue, 1234, "OK", true));

        Assert.Null(await SteamSessions.ReadAsync(Redis, SteamId));
        Assert.Equal(1L, (await Sessions.StatusAsync()).Verdicts["ignored"]);
    }

    [SkippableFact]
    public async Task AnEndRequestEndsTheHeldSession()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS to run");
        await Sessions.OpenAsync(Request("h1"));
        await Sessions.VerdictAsync(_steam.Ok(SteamIdValue));

        await Sessions.EndRequestedAsync(new JsonObject { ["steamId"] = SteamId }.ToJsonString());

        Assert.Equal([SteamIdValue], _steam.Ended);
        var session = await SteamSessions.ReadAsync(Redis, SteamId);
        Assert.Equal((SteamSessions.Canceled, "ended"), (session!.State, session.Response));
        Assert.False(await Redis.HashExistsAsync(SteamSessions.OnlineKey, SteamId));
    }

    [SkippableFact]
    public async Task AnOpenWhileDisconnectedIsUnavailableAndAskedAgainOnceBack()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS to run");
        _steam.IsConnected = false;

        await Sessions.OpenAsync(Request("h1"));

        var session = await SteamSessions.ReadAsync(Redis, SteamId);
        Assert.Equal(SteamSessions.Unavailable, session!.State);
        Assert.Equal(0, _steam.Opens);

        _steam.IsConnected = true;
        await Sessions.ConnectedAsync();
        var queued = await Redis.ListRangeAsync(PlayerMessages.NotificationPrefix + Player);
        Assert.Equal(SteamAuthSessions.ReidentifyNotification, (string?)JsonNode.Parse(Assert.Single(queued).ToString())!["type"]);
    }

    [SkippableFact]
    public async Task AStaleOrMalformedOpenRequestIsDropped()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS to run");

        await Sessions.OpenAsync(Request("h1") with { RequestedAtMs = _time.GetUtcNow().AddMinutes(-11).ToUnixTimeMilliseconds() });
        await Sessions.OpenAsync(Request("h2") with { Ticket = "zz" });
        await Sessions.OpenAsync(Request("h3") with { SteamId = "nope" });

        Assert.Equal(0, _steam.Opens);
        Assert.Null(await SteamSessions.ReadAsync(Redis, SteamId));
    }

    [SkippableFact]
    public async Task PresenceIsSteamsWordOnlyWhereItHasOne()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS to run");
        const string other = "76561198000000092", shared = "76561198000000093", closed = "76561198000000094", old = "76561198000000095";
        var now = _time.GetUtcNow();
        string ms(DateTimeOffset at) => at.ToUnixTimeMilliseconds().ToString();
        await Redis.HashSetAsync(SteamSessions.SessionKey(SteamId), [new HashEntry("state", SteamSessions.Ok), new HashEntry("player_id", Player)]);
        await Redis.HashSetAsync(SteamSessions.SessionKey(shared), [new HashEntry("state", SteamSessions.Ok), new HashEntry("player_id", "someone else")]);
        await Redis.HashSetAsync(SteamSessions.SessionKey(closed), [new HashEntry("state", SteamSessions.Canceled), new HashEntry("player_id", "p4"), new HashEntry("verdict_at", ms(now.AddMinutes(-2)))]);
        await Redis.HashSetAsync(SteamSessions.SessionKey(old), [new HashEntry("state", SteamSessions.Canceled), new HashEntry("player_id", "p5"), new HashEntry("verdict_at", ms(now.AddMinutes(-20)))]);
        List<(string, string)> players = [(Player, SteamId), ("p2", other), ("p3", shared), ("p4", closed), ("p5", old), ("p6", "")];

        // No service connected: nobody has a say.
        Assert.All(await SteamSessions.PresenceAsync(Redis, players, TimeSpan.FromMinutes(5), now), p => Assert.Null(p));

        await Redis.HashSetAsync(SteamSessions.StatusKey, "connected", "1");
        Assert.Equal([true, null, null, false, null, null], await SteamSessions.PresenceAsync(Redis, players, TimeSpan.FromMinutes(5), now));
    }

    private static SteamSessions.OpenRequest Request(string hash, string player = Player)
    {
        byte[] part = new byte[52];
        SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(hash)).AsSpan(0, 8).CopyTo(part);
        return new SteamSessions.OpenRequest(SteamId, player, Ip, Convert.ToHexString(part), hash, new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds());
    }

    private async Task ClearAsync()
    {
        foreach (var server in _redis!.GetServers())
        {
            await foreach (var key in server.KeysAsync(TestRedisDb, "*"))
            {
                await Redis.KeyDeleteAsync(key);
            }
        }
    }

    private sealed class FakeSteam : ISteamAuthClient
    {
        public Dictionary<ulong, uint> Held { get; } = [];

        public List<ulong> Ended { get; } = [];

        public int Opens { get; private set; }

        public bool IsConnected { get; set; } = true;

        public event Action<SteamVerdict>? Verdict;

        public event Action<string>? Disconnected;

        public Task ConnectAsync(CancellationToken ct)
        {
            IsConnected = true;
            return Task.CompletedTask;
        }

        public Task DisconnectAsync()
        {
            IsConnected = false;
            Held.Clear();
            return Task.CompletedTask;
        }

        public uint Open(ulong steamId, ReadOnlyMemory<byte> authPart)
        {
            Opens++;
            uint crc = BitConverter.ToUInt32(authPart.Span);
            Held[steamId] = crc;
            return crc;
        }

        public void End(ulong steamId)
        {
            if (Held.Remove(steamId))
            {
                Ended.Add(steamId);
            }
        }

        public SteamVerdict Ok(ulong steamId, ulong owner = 0) => new(steamId, owner == 0 ? steamId : owner, Held[steamId], "OK", true);

        // The events exist for the loop; the tests call the handlers.
        public void Raise(SteamVerdict verdict) => Verdict?.Invoke(verdict);

        public void Drop(string reason) => Disconnected?.Invoke(reason);
    }

    private sealed class FakeTime(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class Options(SteamSettings value) : IOptionsMonitor<SteamSettings>
    {
        public SteamSettings CurrentValue => value;

        public SteamSettings Get(string? name) => value;

        public IDisposable? OnChange(Action<SteamSettings, string?> listener) => null;
    }
}

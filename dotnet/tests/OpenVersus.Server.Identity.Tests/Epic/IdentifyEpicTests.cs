using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenVersus.Server.Core.Access;
using OpenVersus.Server.Core.Clients;
using OpenVersus.Server.Core.Epic;
using OpenVersus.Server.Core.Identity;
using OpenVersus.Server.Core.Steam;
using OpenVersus.Server.Identity.Epic;
using OpenVersus.Server.Identity.Steam;
using OpenVersus.Server.TestSupport;
using StackExchange.Redis;

namespace OpenVersus.Server.Identity.Tests.Epic;

/// <summary>
/// /api/identify and the Epic id: taken as claimed until Epic:ClientId is set; from then on only the subject of a
/// verified Epic ID token (a scripted verifier here), with a refused token taking a stored proof with it and a token
/// nobody can judge leaving the claim unverified. Against a real Redis (database 3, OVS_TEST_REDIS).
/// </summary>
public sealed class IdentifyEpicTests : IAsyncLifetime
{
    private const int TestRedisDb = 3;
    private const string Ip = "198.51.100.30";
    private const string Install = "0123456789abcdef0123456789abcdef";
    private const string Claimed = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Proved = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Account = "0000000000000000000a0030";
    private const string IdentifySecret = "identify-epic-tests-secret-0123456789abcdef";
    private static readonly string? s_redis = Environment.GetEnvironmentVariable("OVS_TEST_REDIS");
    private readonly EpicSettings _epic = new();
    private readonly ScriptedVerifier _verifier = new();
    private ConnectionMultiplexer? _redis;

    private static bool Configured => !string.IsNullOrEmpty(s_redis);

    private IDatabase Redis => _redis!.GetDatabase();

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
            AllowAdmin = true,
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
    public async Task WithoutAClientIdAnEpicIdIsTakenAsClaimed()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS to run");
        await Redis.StringSetAsync($"identity:epic:{Claimed}", Account);
        var (record, claims, accountId) = await RegisterAsync(new JsonObject { ["epicId"] = Claimed, ["epicToken"] = "whatever", ["installId"] = Install });
        Assert.Equal(Claimed, record["epicId"]);
        Assert.Equal("", record["epicVerified"]);
        Assert.Equal(Claimed, (string?)claims["epicId"]);
        Assert.Equal("", (string?)claims["epicVerified"]);
        Assert.Equal(Account, accountId);
        Assert.Equal(0, _verifier.Calls);
    }

    [SkippableFact]
    public async Task AVerifiedTokenDecidesTheEpicIdAndProvesIt()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS to run");
        _epic.ClientId = "client";
        _verifier.Next = new EpicTokenCheck.Verified(Proved, new JsonObject(), DateTimeOffset.UtcNow.AddHours(1));
        await Redis.StringSetAsync($"identity:epic:{Proved}", Account);
        var (record, claims, accountId) = await RegisterAsync(new JsonObject { ["epicId"] = Claimed, ["epicToken"] = "token", ["installId"] = Install });
        Assert.Equal(Proved, record["epicId"]);
        Assert.Equal("1", record["epicVerified"]);
        Assert.Equal(Proved, (string?)claims["epicId"]);
        Assert.Equal("1", (string?)claims["epicVerified"]);
        Assert.Equal(Account, accountId);
    }

    [SkippableFact]
    public async Task WithAClientIdAClaimWithoutATokenIsDropped()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS to run");
        _epic.ClientId = "client";
        await Redis.StringSetAsync($"identity:epic:{Claimed}", Account);
        var (record, claims, accountId) = await RegisterAsync(new JsonObject { ["epicId"] = Claimed, ["installId"] = Install });
        Assert.Equal("", record["epicId"]);
        Assert.Equal("", (string?)claims["epicId"]);
        Assert.Null(accountId);
        Assert.Equal(0, _verifier.Calls);
    }

    [SkippableFact]
    public async Task ARefusedTokenDropsTheClaimAndAStoredProofForTheSameInstall()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS to run");
        _epic.ClientId = "client";
        _verifier.Next = new EpicTokenCheck.Verified(Proved, new JsonObject(), DateTimeOffset.UtcNow.AddHours(1));
        await RegisterAsync(new JsonObject { ["epicToken"] = "token", ["installId"] = Install });
        Assert.Equal("1", (await Redis.HashGetAsync($"identity:{Ip}", "epicVerified")).ToString());

        _verifier.Next = new EpicTokenCheck.Refused("bad signature");
        var (record, claims, _) = await RegisterAsync(new JsonObject { ["epicId"] = Proved, ["epicToken"] = "replayed", ["installId"] = Install });
        Assert.Equal("", record["epicId"]);
        Assert.Equal("", record["epicVerified"]);
        Assert.Equal("", (string?)claims["epicId"]);
    }

    [SkippableFact]
    public async Task ASecondCallWithoutATokenKeepsTheStoredProofOfTheSameInstall()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS to run");
        _epic.ClientId = "client";
        _verifier.Next = new EpicTokenCheck.Verified(Proved, new JsonObject(), DateTimeOffset.UtcNow.AddHours(1));
        await RegisterAsync(new JsonObject { ["epicToken"] = "token", ["installId"] = Install });

        var (record, claims, _) = await RegisterAsync(new JsonObject { ["installId"] = Install, ["nodePort"] = 7000 });
        Assert.Equal(Proved, record["epicId"]);
        Assert.Equal("1", record["epicVerified"]);
        Assert.Equal("1", (string?)claims["epicVerified"]);

        // Another install at the IP replaces the record, proof and all.
        var (other, _, _) = await RegisterAsync(new JsonObject { ["installId"] = "fedcba9876543210fedcba9876543210" });
        Assert.Equal("", other["epicId"]);
        Assert.Equal("", other["epicVerified"]);
    }

    [SkippableFact]
    public async Task AClaimUnderAKeyOutageDoesNotReplaceAStoredProof()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS to run");
        _epic.ClientId = "client";
        _verifier.Next = new EpicTokenCheck.Verified(Proved, new JsonObject(), DateTimeOffset.UtcNow.AddHours(1));
        await RegisterAsync(new JsonObject { ["epicToken"] = "token", ["installId"] = Install });

        // The late node-port re-send, with the launcher's id claimed, while Epic's keys cannot be had (a restart in an outage).
        _verifier.Next = new EpicTokenCheck.Unavailable("no signing keys from Epic yet");
        var (record, claims, _) = await RegisterAsync(new JsonObject { ["epicId"] = Claimed, ["epicToken"] = "token", ["installId"] = Install, ["nodePort"] = 7000 });
        Assert.Equal(Proved, record["epicId"]);
        Assert.Equal("1", record["epicVerified"]);
        Assert.Equal(Proved, (string?)claims["epicId"]);
        Assert.Equal("1", (string?)claims["epicVerified"]);
    }

    [SkippableFact]
    public async Task ATokenNobodyCanJudgeLeavesTheClaimUnverified()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS to run");
        _epic.ClientId = "client";
        _verifier.Next = new EpicTokenCheck.Unavailable("no signing keys from Epic yet");
        await Redis.StringSetAsync($"identity:epic:{Claimed}", Account);
        var (record, claims, accountId) = await RegisterAsync(new JsonObject { ["epicId"] = Claimed, ["epicToken"] = "token", ["installId"] = Install });
        Assert.Equal(Claimed, record["epicId"]);
        Assert.Equal("", record["epicVerified"]);
        Assert.Equal("", (string?)claims["epicVerified"]);
        // Not resolved by an unproved Epic id.
        Assert.Null(accountId);
    }

    // Helpers

    private async Task<(Dictionary<string, string> Record, JsonObject Claims, string? AccountId)> RegisterAsync(JsonObject body)
    {
        var result = Assert.IsType<IdentifyResult.Ok>(await Service().RegisterAsync(Ip, body));
        var record = (await Redis.HashGetAllAsync($"identity:{Ip}")).ToDictionary(e => e.Name.ToString(), e => e.Value.ToString());
        var claims = IdentifyTokens.Verify((string?)result.Response["token"], IdentifySecret, DateTimeOffset.UtcNow);
        Assert.NotNull(claims);
        return (record, claims, (string?)result.Response["accountId"]);
    }

    private IdentifyService Service()
    {
        var services = new ServiceCollection().AddSingleton<IConnectionMultiplexer>(_redis!).BuildServiceProvider();
        using var key = SteamTickets.NewKey();
        return new IdentifyService(services,
            new Options<AccessSettings>(new AccessSettings { JwtSecret = "identify-epic-tests-game-secret-0123456789", IdentifySecret = IdentifySecret }),
            new Options<ClientSettings>(new ClientSettings { VersionCheck = false }),
            new Options<SteamSettings>(new SteamSettings { Enabled = false }),
            new Options<EpicSettings>(_epic),
            new SteamTicketVerifier(SteamTickets.PublicOf(key)), _verifier, TimeProvider.System, NullLogger<IdentifyService>.Instance);
    }

    private async Task ClearAsync()
    {
        foreach (var endpoint in _redis!.GetEndPoints())
        {
            await _redis.GetServer(endpoint).FlushDatabaseAsync(TestRedisDb);
        }
    }

    private sealed class ScriptedVerifier : IEpicIdTokenVerifier
    {
        public EpicTokenCheck Next { get; set; } = new EpicTokenCheck.Refused("unscripted");
        public int Calls { get; private set; }

        public ValueTask<EpicTokenCheck> CheckAsync(string token, DateTimeOffset now, CancellationToken ct = default)
        {
            Calls++;
            return ValueTask.FromResult(Next);
        }
    }

    private sealed class Options<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}

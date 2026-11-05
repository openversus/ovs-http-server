using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenVersus.Server.Core.Identity;
using OpenVersus.Server.Core.Steam;
using OpenVersus.Server.Identity.Steam;
using OpenVersus.Server.TestSupport;
using StackExchange.Redis;

namespace OpenVersus.Server.Web.Tests;

/// <summary>
/// /api/identify with Steam:Enabled: the verified ticket is queued for the Steam identity service and the verdict waited
/// for. The service is played here by a task that takes the request and writes the session record. Against a real
/// Redis (database 2, OVS_TEST_REDIS); no Mongo (no account resolves).
/// </summary>
public sealed class SteamGateTests : IAsyncLifetime
{
    private const int TestRedisDb = 2;
    private const string Ip = "198.51.100.81";
    private const string Install = "0123456789abcdef0123456789abcdef";
    private static readonly string s_steam = SteamTickets.SteamId.ToString();
    private static readonly string? s_redis = Environment.GetEnvironmentVariable("OVS_TEST_REDIS");
    private static readonly RSA s_key = SteamTickets.NewKey();
    private ConnectionMultiplexer? _redis;

    private static bool Configured => !string.IsNullOrEmpty(s_redis);

    private IDatabase Redis => _redis!.GetDatabase(TestRedisDb);

    private sealed class Factory(string waitMs = "3000") : ServiceFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            string[] parts = s_redis!.Split(':');
            builder.UseSetting("REDIS", parts[0]);
            builder.UseSetting("REDIS_PORT", parts.Length > 1 ? parts[1] : "6379");
            builder.UseSetting("REDIS_USERNAME", Environment.GetEnvironmentVariable("OVS_TEST_REDIS_USER") ?? "");
            builder.UseSetting("REDIS_PW", Environment.GetEnvironmentVariable("OVS_TEST_REDIS_PW") ?? "");
            builder.UseSetting("REDIS_DB", TestRedisDb.ToString());
            builder.UseSetting("Control:Port", "0");
            builder.UseSetting("Control:Socket", "off");
            builder.UseSetting("Steam:Enabled", "true");
            builder.UseSetting("Steam:IdentifyWaitMs", waitMs);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ISteamTicketVerifier>();
                services.AddSingleton<ISteamTicketVerifier>(new SteamTicketVerifier(SteamTickets.PublicOf(s_key)));
            });
        }
    }

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
        });
        await ClearRedisAsync();
    }

    public async Task DisposeAsync()
    {
        if (_redis is not null)
        {
            await ClearRedisAsync();
            _redis.Dispose();
        }
    }

    [SkippableFact]
    public async Task WaitsForSteamsVerdictAndSignsTheLiveProof()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS to run");
        await Redis.HashSetAsync(SteamSessions.StatusKey, "connected", "1");
        await Redis.StringSetAsync($"identity:steam:{s_steam}", "0000000000000000000a0091");
        await using var factory = new Factory();
        var service = AnswerAsync(SteamSessions.Ok);

        var claims = await IdentifyAsync(factory);

        var request = await service;
        Assert.Equal((s_steam, "0000000000000000000a0091", Ip, 104), (request.SteamId, request.PlayerId, request.Ip, request.Ticket.Length));
        Assert.Equal((s_steam, "1", "1"), ((string?)claims["steamId"], (string?)claims["steamVerified"], (string?)claims["steamOnline"]));
        Assert.Equal("1", (string?)await Redis.HashGetAsync($"identity:{Ip}", "steamVerified"));
    }

    [SkippableFact]
    public async Task ARefusalDropsTheSteamIdBeforeTheRecordIsWritten()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS to run");
        await Redis.HashSetAsync(SteamSessions.StatusKey, "connected", "1");
        // The same install's earlier registration, whose verdict never came in time: its proof must not survive the refusal.
        await Redis.HashSetAsync($"identity:{Ip}", [new HashEntry("steamId", s_steam), new HashEntry("steamVerified", "1"), new HashEntry("steamTicket", "{}"), new HashEntry("installId", Install)]);
        await using var factory = new Factory();
        var service = AnswerAsync(SteamSessions.Refused);

        var claims = await IdentifyAsync(factory);

        await service;
        Assert.Equal(("", "", ""), ((string?)claims["steamId"], (string?)claims["steamVerified"], (string?)claims["steamOnline"]));
        var record = (await Redis.HashGetAllAsync($"identity:{Ip}")).ToDictionary(e => e.Name.ToString(), e => e.Value.ToString());
        Assert.Equal(("", "", "", Install), (record["steamId"], record["steamVerified"], record["steamTicket"], record["installId"]));
    }

    [SkippableFact]
    public async Task WithoutAConnectedServiceTheOfflineVerdictStandsAtOnceAndTheTicketWaitsInTheQueue()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS to run");
        await using var factory = new Factory();

        var started = DateTimeOffset.UtcNow;
        var claims = await IdentifyAsync(factory);

        Assert.Equal((s_steam, "1", ""), ((string?)claims["steamId"], (string?)claims["steamVerified"], (string?)claims["steamOnline"]));
        Assert.InRange(DateTimeOffset.UtcNow - started, TimeSpan.Zero, TimeSpan.FromSeconds(2));
        // A service that comes up (or back) takes it from here: no reidentify needed for a short restart.
        Assert.Equal(1, await Redis.ListLengthAsync(SteamSessions.OpenQueue));
    }

    [SkippableFact]
    public async Task NoVerdictInTimeLeavesTheOfflineVerdictAndTheRequestQueued()
    {
        Skip.IfNot(Configured, "set OVS_TEST_REDIS to run");
        await Redis.HashSetAsync(SteamSessions.StatusKey, "connected", "1");
        await using var factory = new Factory(waitMs: "300");

        var claims = await IdentifyAsync(factory);

        Assert.Equal((s_steam, "1", ""), ((string?)claims["steamId"], (string?)claims["steamVerified"], (string?)claims["steamOnline"]));
        Assert.Equal(1, await Redis.ListLengthAsync(SteamSessions.OpenQueue));
    }

    // The Steam identity service: takes the request and answers with the verdict.
    private async Task<SteamSessions.OpenRequest> AnswerAsync(string state)
    {
        for (int i = 0; i < 100; i++)
        {
            var value = await Redis.ListRightPopAsync(SteamSessions.OpenQueue);
            if (SteamSessions.OpenRequest.Parse(value) is { } request)
            {
                await Redis.HashSetAsync(SteamSessions.SessionKey(request.SteamId),
                [
                    new HashEntry("state", state), new HashEntry("response", state == SteamSessions.Ok ? "OK" : "NoLicenseOrExpired"), new HashEntry("ticket_hash", request.Hash),
                    new HashEntry("player_id", ""), new HashEntry("verdict_at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString()),
                ]);
                return request;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("no open request was queued");
    }

    private async Task<JsonObject> IdentifyAsync(Factory factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        client.DefaultRequestHeaders.Add("x-real-ip", Ip);
        var request = new JsonObject { ["steamTicket"] = Convert.ToHexString(SteamTickets.Session(s_key)), ["installId"] = Install, ["clientVersion"] = "2026.10.08.1" };
        var response = await client.PostAsync("/api/identify", new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"));
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        Assert.True((bool?)body["ok"], body.ToJsonString());
        return IdentifyTokens.Verify((string)body["token"]!, ServiceFactory<Program>.IdentifySecret, DateTimeOffset.UtcNow)!;
    }

    private async Task ClearRedisAsync()
    {
        foreach (var server in _redis!.GetServers())
        {
            await foreach (var key in server.KeysAsync(TestRedisDb, "*"))
            {
                await Redis.KeyDeleteAsync(key);
            }
        }
    }
}

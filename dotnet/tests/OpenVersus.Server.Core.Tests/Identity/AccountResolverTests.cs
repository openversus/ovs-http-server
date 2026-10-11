using Microsoft.Extensions.DependencyInjection;
using OpenVersus.Server.Core.Identity;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Tests.Identity;

/// <summary>
/// Which player a request is from (<see cref="IAccountResolver"/>): the verified token's own session, then the ids signed
/// into it, then the one player active at the IP. A request header can no longer reach it (AccountLookup has no field for
/// one); the web test AnIdentityHeaderAloneReadsNobodysNotifications sends x-steam-id end to end. Real Redis, database 15
/// (OVS_TEST_REDIS).
/// </summary>
[Collection(RedisTestDatabase.Name)]
public sealed class AccountResolverTests : IAsyncLifetime
{
    private static readonly string? s_redis = Environment.GetEnvironmentVariable("OVS_TEST_REDIS");
    private const string Mine = "0000000000000000000e0001";
    private const string Other = "0000000000000000000e0002";
    private const string OtherSteam = "76561198000000094";
    private const string Ip = "198.51.100.71";

    private ConnectionMultiplexer? _redis;

    private IDatabase Db => _redis!.GetDatabase();

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

    private Task CleanAsync() => Db.KeyDeleteAsync([$"connections:{Mine}", $"connections:{Other}", $"identity:steam:{OtherSteam}", $"active_ip_accounts:{Ip}"]);

    private IAccountResolver Resolver() =>
        new AccountResolver(new ServiceCollection().AddSingleton<IConnectionMultiplexer>(_redis!).BuildServiceProvider(), TimeProvider.System);

    private Task OnlineAsync(string id) => Db.HashSetAsync($"connections:{id}", [new HashEntry("id", id)]);

    [SkippableFact]
    // The token's own session first, whatever ids it also carries.
    public async Task TheTokensOwnSessionComesFirst()
    {
        Skip.If(_redis is null, "set OVS_TEST_REDIS to run");
        await OnlineAsync(Mine);
        await OnlineAsync(Other);
        await Db.StringSetAsync($"identity:steam:{OtherSteam}", Other);

        var found = await Resolver().ResolveAsync(new AccountLookup(new(Mine, OtherSteam, null, null, null), Ip));

        Assert.Equal((Mine, "jwt"), (found?.Id, found?.Source));
    }

    [SkippableFact]
    // With the token's session gone: the Steam id signed into the token finds its player (the server put it there).
    public async Task ATokensSignedIdFindsItsPlayerOnceItsSessionIsGone()
    {
        Skip.If(_redis is null, "set OVS_TEST_REDIS to run");
        await OnlineAsync(Other);
        await Db.StringSetAsync($"identity:steam:{OtherSteam}", Other);

        var found = await Resolver().ResolveAsync(new AccountLookup(new(Mine, OtherSteam, null, null, null), null));

        Assert.Equal((Other, "steam"), (found?.Id, found?.Source));
    }

    [SkippableFact]
    // Last, the IP: only when exactly one player is active there; with two, nobody.
    public async Task TheIpFindsItsOnePlayerAndNoOneWhenItHasTwo()
    {
        Skip.If(_redis is null, "set OVS_TEST_REDIS to run");
        await OnlineAsync(Other);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await Db.SortedSetAddAsync($"active_ip_accounts:{Ip}", Other, now);

        var found = await Resolver().ResolveAsync(new AccountLookup(null, Ip));
        Assert.Equal((Other, "ip"), (found?.Id, found?.Source));

        await OnlineAsync(Mine);
        await Db.SortedSetAddAsync($"active_ip_accounts:{Ip}", Mine, now);
        Assert.Null(await Resolver().ResolveAsync(new AccountLookup(null, Ip)));
    }
}

using MongoDB.Bson;
using OpenVersus.Server.Core.Rifts;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Tests.Rifts;

/// <summary>
/// The character a player takes into a rift node becomes their session's, as a matchmaking request makes it in the TS
/// server: leaving the rift lobby answers a solo lobby with the session's character, which was Shaggy (none recorded)
/// after a rift played as Wonder Woman. Real Redis (database 15): OVS_TEST_REDIS, OVS_TEST_REDIS_USER, OVS_TEST_REDIS_PW.
/// </summary>
[Collection(RedisTestDatabase.Name)]
public sealed class RiftPlayedLoadoutTests : IAsyncLifetime
{
    private const int TestRedisDb = 15;
    private static readonly string? s_redis = Environment.GetEnvironmentVariable("OVS_TEST_REDIS");

    private readonly string _player = ObjectId.GenerateNewId().ToString();
    private const string Ip = "203.0.113.77";
    private ConnectionMultiplexer? _mux;
    private IDatabase Redis => _mux!.GetDatabase(TestRedisDb);

    public async Task InitializeAsync()
    {
        if (string.IsNullOrEmpty(s_redis))
        {
            return;
        }

        var options = ConfigurationOptions.Parse(s_redis);
        options.User = Environment.GetEnvironmentVariable("OVS_TEST_REDIS_USER") is { Length: > 0 } user ? user : null;
        options.Password = Environment.GetEnvironmentVariable("OVS_TEST_REDIS_PW") is { Length: > 0 } pw ? pw : null;
        _mux = await ConnectionMultiplexer.ConnectAsync(options);
        await Redis.KeyDeleteAsync([$"player:{_player}", $"connections:{_player}", $"connections:{Ip}"]);
    }

    public async Task DisposeAsync()
    {
        if (_mux is not null)
        {
            await Redis.KeyDeleteAsync([$"player:{_player}", $"connections:{_player}", $"connections:{Ip}"]);
            await _mux.DisposeAsync();
        }
    }

    [SkippableFact]
    public async Task TheLockedLoadoutBecomesTheSessionsAndItsIpCopys()
    {
        Skip.If(_mux is null, "set OVS_TEST_REDIS to run");
        await Redis.HashSetAsync($"player:{_player}", [new("character", "character_wonder_woman"), new("skin", "skin_c001_s01"),
            new("profileIcon", "profile_icon_x")]);
        await Redis.HashSetAsync($"connections:{_player}", [new("id", _player), new("current_ip", Ip)]);
        await Redis.HashSetAsync($"connections:{Ip}", [new("id", _player)]);

        var human = await RiftMatchService.HumanAsync(Redis, _player);

        Assert.Equal(("character_wonder_woman", "skin_c001_s01", Ip), (human.Character, human.Skin, human.Ip));
        foreach (string key in new[] { $"connections:{_player}", $"connections:{Ip}" })
        {
            var fields = await Redis.HashGetAsync(key, ["character", "skin", "profileIcon"]);
            Assert.Equal(["character_wonder_woman", "skin_c001_s01", "profile_icon_x"], fields.Select(f => (string?)f));
        }
    }

    [SkippableFact]
    public async Task AnIpCopyAnotherPlayerOwnsIsLeftAlone()
    {
        Skip.If(_mux is null, "set OVS_TEST_REDIS to run");
        await Redis.HashSetAsync($"player:{_player}", [new("character", "character_wonder_woman"), new("skin", "skin_c001_s01")]);
        await Redis.HashSetAsync($"connections:{_player}", [new("id", _player), new("current_ip", Ip)]);
        await Redis.HashSetAsync($"connections:{Ip}", [new("id", "someone_else"), new("character", "character_jason")]);

        await RiftMatchService.HumanAsync(Redis, _player);

        Assert.Equal("character_wonder_woman", (string?)await Redis.HashGetAsync($"connections:{_player}", "character"));
        Assert.Equal("character_jason", (string?)await Redis.HashGetAsync($"connections:{Ip}", "character"));
    }
}

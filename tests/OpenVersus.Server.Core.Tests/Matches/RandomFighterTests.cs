using OpenVersus.Server.Core.Matches;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Tests.Matches;

/// <summary>
/// The Random fighter (<see cref="RandomFighter"/>): a match loadout that is the Random pick becomes a real fighter in one
/// of their skins, kept for the match; the lobby pick stays Random. Against a real Redis (database 15, OVS_TEST_REDIS).
/// </summary>
[Collection(RedisTestDatabase.Name)]
public sealed class RandomFighterTests : IAsyncLifetime
{
    private static readonly string? s_redis = Environment.GetEnvironmentVariable("OVS_TEST_REDIS");
    private const string Me = "0000000000000000001d0001", Bot = "bot_1d0001", Match = "0000000000000000001d0f01";

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

    private Task CleanAsync() => Db.KeyDeleteAsync([$"connections:{Me}", $"player:{Me}", $"bot_config:{Bot}", $"random_pick:{Match}:{Me}", $"random_pick:{Match}:{Bot}"]);

    [SkippableFact]
    public async Task ARandomPickBecomesARealFighterInOneOfTheirSkinsForTheWholeMatch()
    {
        Skip.If(_redis is null, "OVS_TEST_REDIS not set");
        await Db.HashSetAsync($"player:{Me}", [new("character", RandomFighter.Character), new("skin", RandomFighter.Skin)]);
        await Db.HashSetAsync($"connections:{Me}", [new("character", RandomFighter.Character), new("skin", RandomFighter.Skin)]);

        var pick = await RandomFighter.ResolveAsync(Db, Match, Me, $"connections:{Me}", new Random(7));
        Assert.NotNull(pick);
        var fighter = Assert.Single(BotFighters.All, f => f.Character == pick.Value.Character);
        Assert.Contains(pick.Value.Skin, fighter.Skins);
        Assert.Equal(pick.Value.Character, (string?)await Db.HashGetAsync($"connections:{Me}", "character"));
        Assert.Equal(pick.Value.Skin, (string?)await Db.HashGetAsync($"connections:{Me}", "skin"));
        // The lobby pick is untouched: the lobby keeps showing Random; what was played is the real fighter.
        Assert.Equal(RandomFighter.Character, (string?)await Db.HashGetAsync($"player:{Me}", "character"));
        Assert.Equal(pick.Value, await RandomFighter.PlayedAsync(Db, Me));

        // A second build of the same match (a retry, or the set's next game) finds the real fighter: nothing changes.
        Assert.Null(await RandomFighter.ResolveAsync(Db, Match, Me, $"connections:{Me}", new Random(99)));
        Assert.Equal(pick.Value.Character, (string?)await Db.HashGetAsync($"connections:{Me}", "character"));

        // Two builds racing on the same match agree on the first pick.
        await Db.HashSetAsync($"connections:{Me}", "character", RandomFighter.Character);
        var again = await RandomFighter.ResolveAsync(Db, Match, Me, $"connections:{Me}", new Random(1234));
        Assert.Equal(pick, again);
    }

    [SkippableFact]
    public async Task ABotSetToRandomGetsARealFighterAndOtherPicksAreLeftAlone()
    {
        Skip.If(_redis is null, "OVS_TEST_REDIS not set");
        await Db.HashSetAsync($"bot_config:{Bot}", [new("character", RandomFighter.Character), new("skin", RandomFighter.Skin)]);
        var pick = await RandomFighter.ResolveAsync(Db, Match, Bot, $"bot_config:{Bot}", new Random(3));
        Assert.NotNull(pick);
        Assert.Equal(pick.Value.Character, (string?)await Db.HashGetAsync($"bot_config:{Bot}", "character"));
        Assert.False(await Db.KeyExistsAsync($"connections:{Bot}"));

        await Db.HashSetAsync($"connections:{Me}", [new("character", "character_batman"), new("skin", "skin_batman_default")]);
        Assert.Null(await RandomFighter.ResolveAsync(Db, Match, Me, $"connections:{Me}"));
        Assert.Equal("character_batman", (string?)await Db.HashGetAsync($"connections:{Me}", "character"));
        await Db.HashSetAsync($"player:{Me}", [new("character", "character_batman"), new("skin", "skin_batman_default")]);
        Assert.Equal(("character_batman", "skin_batman_default"), await RandomFighter.PlayedAsync(Db, Me));
    }
}

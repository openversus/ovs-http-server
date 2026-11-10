using OpenVersus.Server.Core.Matches;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Tests.Matches;

/// <summary>
/// The Random fighter (<see cref="RandomFighter"/>): a match loadout that is the Random pick becomes a real fighter in one
/// of their skins, kept for the match; the lobby pick stays Random, and the next game of the same players rolls again.
/// Against a real Redis (database 15, OVS_TEST_REDIS).
/// </summary>
[Collection(RedisTestDatabase.Name)]
public sealed class RandomFighterTests : IAsyncLifetime
{
    private static readonly string? s_redis = Environment.GetEnvironmentVariable("OVS_TEST_REDIS");
    private const string Me = "0000000000000000001d0001", Gone = "0000000000000000001d0002", Bot = "bot_1d0001", Match = "0000000000000000001d0f01",
        Next = "0000000000000000001d0f02";

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

    private Task CleanAsync() => Db.KeyDeleteAsync([$"connections:{Me}", $"connections:{Gone}", $"player:{Me}", $"bot_config:{Bot}", $"random_pick:{Match}:{Me}",
        $"random_pick:{Match}:{Bot}", $"random_pick:{Next}:{Me}", RandomFighter.PickersKey(Match), RandomFighter.PickersKey(Next)]);

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

        // A second build of the same match (a retry) finds the real fighter: nothing changes.
        Assert.Null(await RandomFighter.ResolveAsync(Db, Match, Me, $"connections:{Me}", new Random(99)));
        Assert.Equal(pick.Value.Character, (string?)await Db.HashGetAsync($"connections:{Me}", "character"));

        // Two builds racing on the same match agree on the first pick.
        await Db.HashSetAsync($"connections:{Me}", "character", RandomFighter.Character);
        var again = await RandomFighter.ResolveAsync(Db, Match, Me, $"connections:{Me}", new Random(1234));
        Assert.Equal(pick, again);
        // The match keeps who went into it as Random.
        Assert.Equal([Me], await RandomFighter.PickersAsync(Db, Match));
        Assert.InRange((await Db.KeyTimeToLiveAsync(RandomFighter.PickersKey(Match)))!.Value, TimeSpan.FromMinutes(29), TimeSpan.FromMinutes(30));
    }

    [SkippableFact]
    // The game after one, with the same players and no lobby in between (a ranked set's next game, a Casual rematch): the
    // session holds the pick again, so the next config rolls a new fighter; what was played in each match stays known.
    public async Task TheNextGameOfTheSamePlayersRollsAgain()
    {
        Skip.If(_redis is null, "OVS_TEST_REDIS not set");
        await Db.HashSetAsync($"player:{Me}", [new("character", RandomFighter.Character), new("skin", RandomFighter.Skin)]);
        await Db.HashSetAsync($"connections:{Me}", [new("character", RandomFighter.Character), new("skin", RandomFighter.Skin), new("username", "me")]);
        var first = (await RandomFighter.ResolveAsync(Db, Match, Me, $"connections:{Me}", new Random(7)))!.Value;
        // A player of that match whose session is gone: nothing is made for them.
        await Db.SetAddAsync(RandomFighter.PickersKey(Match), Gone);

        Assert.Equal([Me], await RandomFighter.RollAgainAsync(Db, Match));
        Assert.Equal(RandomFighter.Character, (string?)await Db.HashGetAsync($"connections:{Me}", "character"));
        Assert.Equal(RandomFighter.Skin, (string?)await Db.HashGetAsync($"connections:{Me}", "skin"));
        Assert.Equal("me", (string?)await Db.HashGetAsync($"connections:{Me}", "username"));
        Assert.False(await Db.KeyExistsAsync($"connections:{Gone}"));

        // A different seed until the roll differs: the next match's fighter is its own.
        (string Character, string Skin) second = first;
        for (int seed = 0; second == first; seed++)
        {
            await Db.KeyDeleteAsync($"random_pick:{Next}:{Me}");
            await Db.HashSetAsync($"connections:{Me}", [new("character", RandomFighter.Character), new("skin", RandomFighter.Skin)]);
            second = (await RandomFighter.ResolveAsync(Db, Next, Me, $"connections:{Me}", new Random(seed)))!.Value;
        }

        Assert.Equal([Me], await RandomFighter.PickersAsync(Db, Next));
        // Each match's own fighter, whatever the session holds now (missions and stats of the first may come late).
        Assert.Equal(first, await RandomFighter.PlayedAsync(Db, Me, Match));
        Assert.Equal(second, await RandomFighter.PlayedAsync(Db, Me, Next));
        Assert.Equal(second, await RandomFighter.PlayedAsync(Db, Me));

        // A player who did not go in as Random is left alone, a known picker from an earlier game is not.
        await Db.KeyDeleteAsync(RandomFighter.PickersKey(Next));
        Assert.Empty(await RandomFighter.RollAgainAsync(Db, Next));
        Assert.Equal(second.Character, (string?)await Db.HashGetAsync($"connections:{Me}", "character"));
        Assert.Equal([Me], await RandomFighter.RollAgainAsync(Db, Next, [Me]));
        Assert.Equal(RandomFighter.Character, (string?)await Db.HashGetAsync($"connections:{Me}", "character"));
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

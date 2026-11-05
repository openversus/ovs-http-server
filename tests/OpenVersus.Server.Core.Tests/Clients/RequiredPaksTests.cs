using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenVersus.Server.Core.Clients;
using OpenVersus.Server.Core.Matches;
using OpenVersus.Server.Core.Realtime;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Tests.Clients;

/// <summary>
/// The required paks (<see cref="RequiredPaksRefresher"/>, <see cref="PakUpdateNotices"/>): the X-OVS-Paks form, a
/// release's list, and who is told to restart. The Redis ones against a real Redis (database 15, OVS_TEST_REDIS).
/// </summary>
[Collection(RedisTestDatabase.Name)]
public sealed class RequiredPaksTests : IAsyncLifetime
{
    private static readonly string? s_redis = Environment.GetEnvironmentVariable("OVS_TEST_REDIS");
    private static string Id(int n) => $"0000000000000000001c{n:D4}";
    private static readonly string Same = Id(1), Stale = Id(2), Playing = Id(3), Unreported = Id(4), InLobby = Id(5), Queued = Id(6), InSet = Id(7);
    private const string Literal = "OVS_P=AAAA;OVS_Experimental_50_P=bbbb";

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

    private async Task CleanAsync()
    {
        var server = _redis!.GetServer(_redis.GetEndPoints()[0]);
        foreach (var key in server.Keys(15, "*0000000000000000001c*"))
        {
            await Db.KeyDeleteAsync(key);
        }

        await Db.KeyDeleteAsync([RequiredPaks.Key, RequiredPaksRefresher.LockKey]);
        foreach (string id in new[] { Same, Stale, Playing, Unreported, InLobby, Queued, InSet })
        {
            await Db.SetRemoveAsync("online_players", id);
        }
    }

    private IDatabase Db => _redis!.GetDatabase();

    private IServiceProvider Services(string requiredPaks) => new ServiceCollection()
        .AddSingleton<IConnectionMultiplexer>(_redis!)
        .AddSingleton<Microsoft.Extensions.Options.IOptionsMonitor<ClientSettings>>(new TestOptions<ClientSettings>(new ClientSettings { RequiredPaks = requiredPaks }))
        .AddSingleton(TimeProvider.System)
        .AddSingleton<IRequiredPaks, RequiredPaks>()
        .BuildServiceProvider();

    private static RequiredPaksRefresher Refresher(IServiceProvider services) =>
        new(services, services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<ClientSettings>>(), TimeProvider.System,
            NullLogger<RequiredPaksRefresher>.Instance);

    private async Task<List<JsonObject>> NoticesAsync(string id) =>
        [.. (await Db.ListRangeAsync(PlayerMessages.NotificationPrefix + id)).Select(v => JsonNode.Parse(v.ToString())!.AsObject())];

    [Fact]
    public void AReportIsExactlyTheRequiredPaks()
    {
        var required = PakReport.Parse(Literal);
        Assert.Equal("aaaa", required["ovs_p"]);
        Assert.True(PakReport.Matches(required, PakReport.Parse(" ovs_experimental_50_p=BBBB ; OVS_P=aaaa ")));
        // Missing, extra and different paks all differ.
        Assert.False(PakReport.Matches(required, PakReport.Parse("OVS_P=aaaa")));
        Assert.False(PakReport.Matches(required, PakReport.Parse(Literal + ";OVS_Extra_60_P=cc")));
        Assert.False(PakReport.Matches(required, PakReport.Parse("OVS_P=aaab;OVS_Experimental_50_P=bbbb")));
        // Malformed items are skipped.
        Assert.Empty(PakReport.Parse("=x;y=;noequals"));
        Assert.Equal("OVS_Experimental_50_P=bbbb;OVS_P=aaaa", PakReport.Format(required));
    }

    [Fact]
    public void AReleaseRequiresEachPakAtItsUtocOrItsPak()
    {
        static ClientUpdateFile File(string name, string sha) => new(name, name.EndsWith(".asi") ? "plugin" : "paks", 1, sha, "https://x/" + name);
        var required = PakReport.FromRelease([
            File("OVS_P.pak", "11"), File("OVS_P.utoc", "22"), File("OVS_P.ucas", "33"), File("OVS_P.sig", "44"),
            File("OVS_Plain_P.pak", "55"), File("OpenVersus.asi", "66")]);
        Assert.Equal(2, required.Count);
        Assert.Equal("22", required["OVS_P"]);
        Assert.Equal("55", required["OVS_Plain_P"]);
    }

    [Fact]
    public void TheStoredListReadsBack()
    {
        var list = new RequiredPakList("2026.10.14.01", PakReport.Parse(Literal));
        var back = RequiredPaks.Read(RequiredPaks.Write(list));
        Assert.Equal("2026.10.14.01", back!.Version);
        Assert.True(PakReport.Matches(list.Paks, back.Paks));
        Assert.Null(RequiredPaks.Read("not json"));
        Assert.Null(RequiredPaks.Read("{\"version\":\"x\",\"paks\":{}}"));
    }

    [SkippableFact]
    public async Task ANewListTellsTheOnlinePlayersWhosePaksDifferAndAreNotBusy()
    {
        Skip.If(_redis is null, "OVS_TEST_REDIS not set");
        foreach (string id in new[] { Same, Stale, Playing, Unreported, InLobby, Queued, InSet })
        {
            await Db.SetAddAsync("online_players", id);
        }

        // A custom lobby's pregame, a queue and the break between a ranked set's games are busy too.
        await Db.HashSetAsync($"connections:{InSet}", "paks", "OVS_P=aaaa");
        await Db.StringSetAsync($"player_ranked_set:{InSet}", "set");
        await Db.HashSetAsync($"connections:{InLobby}", "paks", "OVS_P=aaaa");
        await Db.StringSetAsync($"ssc_custom_lobby_player:{InLobby}", "lobby");
        await Db.HashSetAsync($"connections:{Queued}", "paks", "OVS_P=aaaa");
        await Db.HashSetAsync($"player:{Queued}", "status", "queued");

        await Db.HashSetAsync($"connections:{Same}", "paks", "OVS_Experimental_50_P=bbbb;OVS_P=aaaa");
        await Db.HashSetAsync($"connections:{Stale}", "paks", "OVS_Experimental_50_P=old0;OVS_P=aaaa");
        await Db.HashSetAsync($"connections:{Playing}", "paks", "OVS_P=aaaa");
        await Db.StringSetAsync(GameplayConfigs.Key(Playing), "{}");

        var services = Services(Literal);
        Assert.True(await Refresher(services).RefreshAsync());
        Assert.Empty(await NoticesAsync(Same));
        Assert.Empty(await NoticesAsync(Playing));
        Assert.Empty(await NoticesAsync(Unreported));
        Assert.Empty(await NoticesAsync(InLobby));
        Assert.Empty(await NoticesAsync(Queued));
        Assert.Empty(await NoticesAsync(InSet));
        var told = Assert.Single(await NoticesAsync(Stale));
        Assert.Equal(PakUpdateNotices.Type, told["type"]!.GetValue<string>());
        Assert.Equal(new ClientSettings().PaksUpdateMessage, told["message"]!.GetValue<string>());

        // The same list again changes nothing and tells nobody again.
        Assert.False(await Refresher(services).RefreshAsync());
        Assert.Single(await NoticesAsync(Stale));

        // The player in the match is told at its end, once.
        await Db.KeyDeleteAsync(GameplayConfigs.Key(Playing));
        Assert.Equal(1, await PakUpdateNotices.MatchEndedAsync(services, Db, [Playing, Same], TimeProvider.System));
        Assert.Equal(0, await PakUpdateNotices.MatchEndedAsync(services, Db, [Playing, Same], TimeProvider.System));
        Assert.Single(await NoticesAsync(Playing));
    }

    [SkippableFact]
    public async Task OffTakesTheListAwayAndTellsNobody()
    {
        Skip.If(_redis is null, "OVS_TEST_REDIS not set");
        await Db.SetAddAsync("online_players", Stale);
        await Db.HashSetAsync($"connections:{Stale}", "paks", "OVS_P=old0");
        Assert.True(await Refresher(Services(Literal)).RefreshAsync());
        await Db.KeyDeleteAsync(PlayerMessages.NotificationPrefix + Stale);

        var off = Services("off");
        Assert.True(await Refresher(off).RefreshAsync());
        Assert.False((await Db.StringGetAsync(RequiredPaks.Key)).HasValue);
        Assert.Null(await off.GetRequiredService<IRequiredPaks>().CurrentAsync());
        Assert.Equal(0, await PakUpdateNotices.MatchEndedAsync(off, Db, [Stale], TimeProvider.System));
        Assert.Empty(await NoticesAsync(Stale));
    }
}

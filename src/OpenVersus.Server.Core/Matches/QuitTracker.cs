using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenVersus.Server.Core.Realtime;
using OpenVersus.Server.Core.Settings;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Matches;

// Walking out of a matchmade match (docs/BANS.md, "Walking out of matches"). The OpenVersus client reports every exit the
// game asks for itself (Alt+F4, the window's close button; a kill from outside is not seen) to POST /ovs/quit before
// the game goes. On those screens (the perk selection, the match, the rematch vote) there is no menu to quit by, so an
// exit there is a walk-out. Only the FIRST player to leave a match (or set) is counted: the game of whoever is left
// can hang waiting for them, and their only way out is the same exit. Three in a rolling 30 minutes lock the player
// out of matchmaking for 15 minutes: their ticket is refused and a banner says so. Custom lobbies never count, nor a
// match against bots alone.
//
//   player:{id} status           in_match from the match being found to its end (MatchmakingQueue, MatchStatusEvents)
//   casual_rematch_player:{id}   while a Casual rematch vote is open (Rematches)
//   player_ranked_set:{id}       the ranked set, from its first game to its end (RankedSets)
//   quit_group:{id}              the match found for this player, written with it (MatchmakingQueue.FoundAsync):
//                                what the first-leaver rule is per; quit_group_humans:{group} how many humans it has
//   quit_leaver:{group}          the first player who left it
//   quits:{id}                   when the player's counted quits happened (a sorted set, trimmed to the window)
//   quit_lockout:{id}            until when matchmaking is refused (ms), with the lockout as its TTL
//   the account (Mongo)          ragequit_total: every counted quit, for good; ragequit_put_in_timeout: every lockout

/// <summary>Walking out of matches: how many in how long lock a player out of matchmaking, and for how long.</summary>
public sealed class QuitSettings
{
    [Description("On: a quit the client reports from a matchmade match counts, and enough of them lock the player out of matchmaking. Off: reports are logged and nothing else.")]
    public bool Enabled { get; set; } = true;

    [Description("How many counted quits within WindowMinutes lock the player out.")]
    [Range(1, 100)]
    public int Limit { get; set; } = 3;

    [Description("The rolling window the quits are counted in, in minutes.")]
    [Range(1, 1440)]
    public int WindowMinutes { get; set; } = 30;

    [Description("How long matchmaking is refused, in minutes.")]
    [Range(1, 1440)]
    public int LockoutMinutes { get; set; } = 15;

    [Description("The banner's title, shown when a locked-out player asks for a match.")]
    public string BannerTitle { get; set; } = "Go touch some grass";

    [Description("The banner's text; {minutes} is how long the lockout has left.")]
    public string BannerMessage { get; set; } = "You have disconnected from several matches in a short period of time.\nUse the next {minutes} minutes to touch some grass and learn to stop being a weenie.";

    [Description("How long the banner shows, in seconds.")]
    [Range(1, 300)]
    public int BannerSeconds { get; set; } = 10;
}

/// <summary>What a report came to: whether it counted and why not otherwise, the count in the window, and the lockout if one holds.</summary>
public sealed record QuitVerdict(bool Counted, string Reason, int Count, DateTimeOffset? LockedUntil);

public interface IQuitTracker
{
    /// <summary>The client of <paramref name="playerId"/> reported that the game asked to exit.</summary>
    Task<QuitVerdict> ReportAsync(string playerId, DateTimeOffset now);

    /// <summary>Until when <paramref name="playerId"/> is locked out of matchmaking, or null.</summary>
    Task<DateTimeOffset?> LockedUntilAsync(string playerId, DateTimeOffset now);

    /// <summary>How many counted quits <paramref name="playerId"/> has in the window.</summary>
    Task<int> RecentAsync(string playerId, DateTimeOffset now);

    /// <summary>
    /// Whether any of <paramref name="playerIds"/> is locked out: each one is sent the banner and the refusal is logged
    /// as "Refused <paramref name="what"/>"; the caller then answers as it does an outdated client.
    /// </summary>
    Task<bool> RefuseLockedAsync(IEnumerable<string> playerIds, ILogger log, string what);
}

internal sealed class QuitTracker(IServiceProvider services, IOptionsMonitor<QuitSettings> settings, ILogger<QuitTracker> log) : IQuitTracker
{
    public const string GroupPrefix = "quit_group:";
    public const string GroupHumansPrefix = "quit_group_humans:";
    public const string LeaverPrefix = "quit_leaver:";
    public const string QuitsPrefix = "quits:";
    public const string LockoutPrefix = "quit_lockout:";
    /// <summary>The account's lifetime count of counted quits.</summary>
    public const string TotalField = "ragequit_total";
    /// <summary>The account's lifetime count of matchmaking lockouts for them.</summary>
    public const string TimeoutsField = "ragequit_put_in_timeout";

    /// <summary>How long a found match's group is remembered: longer than a set with its rematches.</summary>
    public static readonly TimeSpan GroupTtl = TimeSpan.FromMinutes(45);

    /// <summary>MatchmakingQueue.FoundAsync: the players matched together, under one group, with how many of them are human.</summary>
    public static async Task GroupAsync(IDatabase redis, IReadOnlyList<string> players)
    {
        string group = MongoDB.Bson.ObjectId.GenerateNewId().ToString();
        int humans = players.Distinct().Count(p => !p.StartsWith("Bot", StringComparison.OrdinalIgnoreCase));
        await redis.StringSetAsync(GroupHumansPrefix + group, humans, GroupTtl);
        foreach (string player in players.Distinct())
        {
            await redis.StringSetAsync(GroupPrefix + player, group, GroupTtl);
        }
    }

    public async Task<QuitVerdict> ReportAsync(string playerId, DateTimeOffset now)
    {
        var redis = Redis();
        var current = settings.CurrentValue;
        var locked = await LockedUntilAsync(redis, playerId, now);
        // The context: a ranked set is a group of its own; a Casual rematch vote or a found match still under way goes by
        // the group the match was found with. Nothing else (the menus, a custom lobby, a match the server never made) counts.
        string ranked = (await redis.StringGetAsync($"player_ranked_set:{playerId}")).ToString();
        string status = (await redis.HashGetAsync($"player:{playerId}", "status")).ToString();
        bool rematch = await redis.KeyExistsAsync(RematchVotes.CasualPlayerKey(playerId));
        string group = ranked.Length > 0 ? "set:" + ranked : (await redis.StringGetAsync(GroupPrefix + playerId)).ToString();
        if (group.Length == 0 || (ranked.Length == 0 && status != "in_match" && !rematch))
        {
            log.LogInformation("Quit reported by {Player}: not in a matchmade match (status {Status}); not counted", playerId, status.Length > 0 ? status : "-");
            return new QuitVerdict(false, "not in a matchmade match", await RecentAsync(redis, playerId, now), locked);
        }

        if (ranked.Length == 0 && int.TryParse((await redis.StringGetAsync(GroupHumansPrefix + group)).ToString(), out int humans) && humans < 2)
        {
            log.LogInformation("Quit reported by {Player} from a match against bots alone; not counted", playerId);
            return new QuitVerdict(false, "a match against bots", await RecentAsync(redis, playerId, now), locked);
        }

        if (!await redis.StringSetAsync(LeaverPrefix + group, playerId, GroupTtl, When.NotExists) && (await redis.StringGetAsync(LeaverPrefix + group)).ToString() != playerId)
        {
            log.LogInformation("Quit reported by {Player} from {Group} after {First} already left it; not counted (their game may be stuck)", playerId, group, (await redis.StringGetAsync(LeaverPrefix + group)).ToString());
            return new QuitVerdict(false, "not the first to leave", await RecentAsync(redis, playerId, now), locked);
        }

        if (!current.Enabled)
        {
            log.LogInformation("Quit reported by {Player} from {Group}: the first to leave; counting is off (Quits:Enabled)", playerId, group);
            return new QuitVerdict(false, "counting is off", await RecentAsync(redis, playerId, now), locked);
        }

        string key = QuitsPrefix + playerId;
        var window = TimeSpan.FromMinutes(current.WindowMinutes);
        await redis.SortedSetAddAsync(key, now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture), now.ToUnixTimeMilliseconds());
        await redis.SortedSetRemoveRangeByScoreAsync(key, double.NegativeInfinity, (now - window).ToUnixTimeMilliseconds());
        await redis.KeyExpireAsync(key, window);
        int count = (int)await redis.SortedSetLengthAsync(key);
        bool lockedNow = false;
        if (count >= current.Limit && locked is null)
        {
            locked = now + TimeSpan.FromMinutes(current.LockoutMinutes);
            lockedNow = true;
            await redis.StringSetAsync(LockoutPrefix + playerId, locked.Value.ToUnixTimeMilliseconds(), TimeSpan.FromMinutes(current.LockoutMinutes));
            log.LogWarning("Player {Player} walked out of {Count} matchmade matches within {Window} min (the last: {Group}): matchmaking refused until {Until:u}", playerId, count, current.WindowMinutes, group, locked.Value);
        }
        else
        {
            log.LogWarning("Player {Player} walked out of matchmade match {Group}: {Count} of {Limit} within {Window} min", playerId, group, count, current.Limit, current.WindowMinutes);
        }

        await CountOnAccountAsync(playerId, lockedNow);
        return new QuitVerdict(true, "the first to leave", count, locked);
    }

    // The lifetime counters on the account: every counted quit, and every lockout. Without Mongo here, logged and skipped.
    private async Task CountOnAccountAsync(string playerId, bool lockedNow)
    {
        if (services.GetService<IMongoDatabase>() is not { } mongo || !ObjectId.TryParse(playerId, out var id))
        {
            log.LogWarning("The walk-out of {Player} is not counted on the account: this service has no Mongo (MONGODB_URI)", playerId);
            return;
        }

        try
        {
            var inc = new BsonDocument(TotalField, 1);
            if (lockedNow)
            {
                inc[TimeoutsField] = 1;
            }

            await mongo.GetCollection<BsonDocument>(Access.PlayerRecord.Collection).UpdateOneAsync(new BsonDocument("_id", id), new BsonDocument("$inc", inc));
        }
        catch (Exception e) when (e is MongoException or TimeoutException)
        {
            log.LogError(e, "The walk-out of {Player} could not be counted on the account", playerId);
        }
    }

    public Task<DateTimeOffset?> LockedUntilAsync(string playerId, DateTimeOffset now) => LockedUntilAsync(Redis(), playerId, now);

    public Task<int> RecentAsync(string playerId, DateTimeOffset now) => RecentAsync(Redis(), playerId, now);

    public async Task<bool> RefuseLockedAsync(IEnumerable<string> playerIds, ILogger caller, string what)
    {
        var redis = Redis();
        var now = DateTimeOffset.UtcNow;
        bool refused = false;
        foreach (string id in playerIds.Distinct())
        {
            if (await LockedUntilAsync(redis, id, now) is not { } until)
            {
                continue;
            }

            refused = true;
            int minutes = Math.Max(1, (int)Math.Ceiling((until - now).TotalMinutes));
            var current = settings.CurrentValue;
            caller.LogWarning("Refused {What}: {Player} is locked out of matchmaking for walking out of matches, {Minutes} min left", what, id, minutes);
            await PlayerMessages.NotifyClientAsync(redis, id, "admin_banner", current.BannerTitle, current.BannerMessage.Replace("{minutes}", minutes.ToString(CultureInfo.InvariantCulture)),
                new JsonObject { ["timeout"] = current.BannerSeconds }, now.ToUnixTimeMilliseconds());
        }

        return refused;
    }

    private static async Task<DateTimeOffset?> LockedUntilAsync(IDatabase redis, string playerId, DateTimeOffset now)
    {
        var value = await redis.StringGetAsync(LockoutPrefix + playerId);
        if (!value.HasValue || !long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long ms))
        {
            return null;
        }

        var until = DateTimeOffset.FromUnixTimeMilliseconds(ms);
        return until > now ? until : null;
    }

    private async Task<int> RecentAsync(IDatabase redis, string playerId, DateTimeOffset now) =>
        (int)await redis.SortedSetLengthAsync(QuitsPrefix + playerId, (now - TimeSpan.FromMinutes(settings.CurrentValue.WindowMinutes)).ToUnixTimeMilliseconds(), double.PositiveInfinity);

    private IDatabase Redis() => services.GetService<IConnectionMultiplexer>()?.GetDatabase() ?? throw new InvalidOperationException("this service has no Redis (REDIS)");
}

public static class QuitTrackerHosting
{
    /// <summary>The quit tracker (the Quits settings): the web service's report route and the lobbies service's ticket refusal.</summary>
    public static WebApplicationBuilder AddQuitTracker(this WebApplicationBuilder builder)
    {
        builder.AddSetting<QuitSettings>("Quits");
        builder.Services.TryAddSingleton<IQuitTracker, QuitTracker>();
        return builder;
    }
}

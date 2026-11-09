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
// exit there is a walk-out. Only the FIRST player to leave a match (with the rematches that follow it; or a set) is
// counted: the game of whoever is left can hang waiting for them, and their only way out is the same exit. Three in a
// rolling 30 minutes lock the player out of matchmaking for 15 minutes: their ticket is refused and a banner says so.
// Custom lobbies never count, nor a match against bots alone.
//
// An unranked match (Casual, Testing Grounds) is played again by its rematch vote, game after game, and each of those
// games counts as the first does: the rematch puts its players in_match again (it has no ticket to do it: Rematches)
// and stays in the first game's group, so a player who walks out just as it starts leaves the other, facing nobody,
// not the first to leave.
//
// Whatever the verdict (a custom lobby's quit never counts), a quit while a rematch vote the player has a say in is open
// is their decline: the vote is over for everyone (Rematches.WalkedOutAsync), and no rematch starts without them.
//
//   player:{id} status           in_match from the match being found to its end (MatchmakingQueue, MatchStatusEvents),
//                                and from a rematch's start to its end (Rematches)
//   casual_rematch_player:{id}   while a Casual rematch vote is open (Rematches)
//   player_ranked_set:{id}       the ranked set, from its first game to its end (RankedSets)
//   quit_group:{id}              the match found for this player, written with it (MatchmakingQueue.FoundAsync) and kept
//                                by its rematches: what the first-leaver rule is per; quit_group_humans:{group} how
//                                many humans it has
//   quit_rematch:{match}         a rematch on its way to being found (Rematches): it keeps its players' group
//   quit_leaver:{group}          the first player who left it
//   quits:{id}                   when the player's counted quits happened (a sorted set, trimmed to the window)
//   quit_lockout:{id}            until when matchmaking is refused (ms), with the lockout as its TTL
//   the account (Mongo)          ragequit_total: every counted quit, for good; ragequit_put_in_timeout: every lockout

/// <summary>Walking out of matches: how many in how long lock a player out of matchmaking, and for how long.</summary>
public sealed class QuitSettings
{
    [Description("On: a quit the client reports from a matchmade match counts, and enough of them lock the player out of matchmaking. Off: reports are logged and nothing counts. Either way a quit during a rematch vote declines the rematch for everyone.")]
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
    public const string RematchPrefix = "quit_rematch:";
    public const string QuitsPrefix = "quits:";
    public const string LockoutPrefix = "quit_lockout:";
    /// <summary>The account's lifetime count of counted quits.</summary>
    public const string TotalField = "ragequit_total";
    /// <summary>The account's lifetime count of matchmaking lockouts for them.</summary>
    public const string TimeoutsField = "ragequit_put_in_timeout";

    /// <summary>How long a found match's group is remembered: longer than a set with its rematches.</summary>
    public static readonly TimeSpan GroupTtl = TimeSpan.FromMinutes(45);

    /// <summary>How long a rematch may take from its launch to being found: as long as the launch itself is kept (MatchLaunches).</summary>
    public static readonly TimeSpan RematchTtl = TimeSpan.FromMinutes(20);

    /// <summary>Rematches, before the launch: the match that starts as <paramref name="matchId"/> is a rematch, and keeps its players' group.</summary>
    public static Task RematchAsync(IDatabase redis, string matchId) => redis.StringSetAsync(RematchPrefix + matchId, "1", RematchTtl);

    /// <summary>The rematch named <paramref name="matchId"/> was never launched.</summary>
    public static Task NoRematchAsync(IDatabase redis, string matchId) => redis.KeyDeleteAsync(RematchPrefix + matchId);

    /// <summary>
    /// MatchmakingQueue.FoundAsync: the players matched together, under one group, with how many of them are human. A
    /// rematch (<see cref="RematchAsync"/>, by <paramref name="matchId"/>) whose humans are all still in one group together
    /// stays in it, for <see cref="GroupTtl"/> more: whoever left it first, at the vote too, stays the only one counted.
    /// </summary>
    public static async Task GroupAsync(IDatabase redis, IReadOnlyList<string> players, string? matchId = null)
    {
        if (matchId is not null && await redis.KeyDeleteAsync(RematchPrefix + matchId))
        {
            var playing = players.Distinct().Where(p => !p.StartsWith("Bot", StringComparison.OrdinalIgnoreCase)).ToList();
            var groups = new HashSet<string>();
            foreach (string player in playing)
            {
                groups.Add((await redis.StringGetAsync(GroupPrefix + player)).ToString());
            }

            if (groups.Count == 1 && groups.Single() is { Length: > 0 } kept)
            {
                foreach (string player in playing)
                {
                    await redis.KeyExpireAsync(GroupPrefix + player, GroupTtl);
                }

                await redis.KeyExpireAsync(GroupHumansPrefix + kept, GroupTtl);
                await redis.KeyExpireAsync(LeaverPrefix + kept, GroupTtl);
                return;
            }
        }

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
        var verdict = await CountAsync(playerId, now);
        // The game is going, whatever the verdict: a rematch vote the player has a say in is over for everyone, as by
        // their decline. After the verdict, which reads the vote.
        if (await Rematches.WalkedOutAsync(Redis(), playerId) is { } vote)
        {
            log.LogInformation("Quit reported by {Player} during the rematch vote of {Vote}: the rematch is declined for everyone", playerId, vote);
        }

        return verdict;
    }

    private async Task<QuitVerdict> CountAsync(string playerId, DateTimeOffset now)
    {
        var redis = Redis();
        var current = settings.CurrentValue;
        var locked = await LockedUntilAsync(redis, playerId, now);
        // The context: a ranked set is a group of its own; a Casual rematch vote or a found match still under way (its
        // rematches too) goes by the group the match was found with. Nothing else (the menus, a custom lobby, a match the
        // server never made) counts.
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

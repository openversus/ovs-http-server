using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using OpenVersus.Server.Core.Missions;
using OpenVersus.Server.Core.Realtime;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.RewardTracks;

// End Game's ranked-set XP. Whoever settles a ranked set (or a public FFA game) decides who is paid and whether they won
// (RankedSetXpPayout, the rules of the TS server's rankedSetXpService.ts): nothing before a game is played, so a pregame
// dodge pays nobody; after one, a player who quit gets nothing, everyone else is paid. It appends {playerId, won,
// character, setKey, source} for each player to match:results (field set_xp), and the match flow's consumer of that
// stream (MatchResultStream) hands each record to the payer here.
// Once per player and set:
//
//   - the battle pass (mrt_battlepass_season_five) gains RewardTracks:BattlePassSetXp, +BattlePassWinXp for a win;
//   - the account level (mrt_mastery_account) and the played character's level (its CharacterData MrtSlug, which
//     carries its Fighter Pass) gain CharacterSetXp, +CharacterWinXp;
//   - the account and character levels' newly completed tiers are paid at once, as the Fighter Passes are not claimed
//     by the player (toasts; tiers 5, 10 and 15 pay battle pass XP, which goes to the battle pass by its tag; the last
//     pays the character's Chromium skin);
//   - the game is sent RewardTrackStatesUpdated (UpdateContext XpReward, a banner each) with the tracks that moved but
//     the account level, which it is sent alone with UpdateContext Unknown (no banner; live showed only the battle pass).
//
// Only tracks that are the player's own move (RewardTracks:PerPlayer, CharacterMastery: AddScoreAsync). This is the
// only source of level XP in End Game: RewardTracks:MatchXp and RiftMatchXp are off there, so custom games and rifts
// earn none.
//
// A record is paid, or it stays pending on the stream and is paid on its retry: the claim ranked_set_xp:{setKey}:{playerId}
// is taken first and given back when nothing was paid (the score not written: an error, or the player's tracks kept
// changing), and the record's failure is left to the stream. Once the score is written, a later failure (the tiers, the
// game's update) is only logged: a retry would pay the XP twice.
//
// Redis, read        match:results set_xp records (through MatchResultStream)
// Redis, written     ranked_set_xp:{setKey}:{playerId} NX EX 7 days (one payment per player and set)
// Mongo, written     rewardtracks (RewardTrackService), playeritems / playercounters (RewardGrants)
// Published          ws:send RewardTrackStatesUpdated

/// <summary>Pays End Game's ranked-set XP, one set_xp record of match:results at a time (see the header).</summary>
internal interface IRankedSetXpPayer
{
    /// <summary>
    /// Pays one record. Throws when nothing was paid and a retry may pay it (the stream keeps the record pending); returns
    /// for a record paid, already paid, unreadable, or paid whose tiers or game update then failed.
    /// </summary>
    Task PayAsync(string record);
}

internal sealed class RankedSetXpPayer(IServiceProvider services, IOptionsMonitor<RewardTrackSettings> settings,
    IOptionsMonitor<MissionSettings> missions, IRewardTrackService tracks, IRewardGrants grants, ILogger<RankedSetXpPayer> log) : IRankedSetXpPayer
{
    internal const string BattlePass = "mrt_battlepass_season_five";
    internal const string Account = "mrt_mastery_account";
    // EMvsRewardTrackUpdateContext: XpReward shows a banner per track; Unknown updates the state only.
    private const int XpReward = 6;
    private const int Quiet = 0;

    // One payment at a time: a player's tracks are written read-modify-write (version-guarded, three tries), so two of
    // their payments at once could lose one (eight at once lost four, 2026-10-05). The stream's consumer hands over one
    // record at a time already; this keeps it so for any other caller.
    private readonly SemaphoreSlim _one = new(1, 1);

    public async Task PayAsync(string record)
    {
        await _one.WaitAsync();
        try
        {
            await HandleAsync(record);
        }
        finally
        {
            _one.Release();
        }
    }

    // AddScoreAsync answers no tracks when its write kept losing to other writes (another request for the player): tried
    // again a few times, a little later each time.
    private async Task<IReadOnlyList<JsonObject>> AddScoreAsync(string playerId, IReadOnlyDictionary<string, int> points)
    {
        for (int attempt = 0; ; attempt++)
        {
            var changed = await tracks.AddScoreAsync(playerId, points, CancellationToken.None);
            if (changed.Count > 0 || points.Count == 0 || attempt == 4)
            {
                return changed;
            }

            await Task.Delay(50 * (attempt + 1));
        }
    }

    internal async Task HandleAsync(string message)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(message);
        }
        catch (JsonException e)
        {
            log.LogError("Ranked-set XP record {Message} cannot be read ({Error}); dropped", message, e.Message);
            return;
        }

        if (node is not JsonObject set || Text(set["playerId"]) is not { } playerId || !ObjectId.TryParse(playerId, out _) || Text(set["setKey"]) is not { } setKey)
        {
            log.LogError("Ranked-set XP record {Message} names no player or set; dropped", message);
            return;
        }

        var redis = services.GetService<IConnectionMultiplexer>()?.GetDatabase() ?? throw new InvalidOperationException("this service has no Redis (REDIS)");
        string claim = $"ranked_set_xp:{setKey}:{playerId}";
        if (!await redis.StringSetAsync(claim, "1", TimeSpan.FromDays(7), When.NotExists))
        {
            return;
        }

        string source = Text(set["source"]) ?? setKey;
        bool won = set["won"] is JsonValue w && w.TryGetValue(out bool b) && b;
        string character = Text(set["character"]) ?? "";
        var points = Points(settings.CurrentValue, won, character);
        Dictionary<string, JsonObject> changed;
        try
        {
            changed = (await AddScoreAsync(playerId, points)).ToDictionary(t => t["TrackSlug"]!.GetValue<string>());
            if (changed.Count == 0 && points.Count > 0)
            {
                throw new InvalidOperationException("the player's tracks kept changing");
            }
        }
        catch (Exception e)
        {
            // Nothing was paid: the claim is given back, and the record's retry pays.
            await redis.KeyDeleteAsync(claim);
            log.LogWarning("Ranked-set XP for {Player} from {Source} not paid yet ({Error}); the record stays pending", playerId, source, e.Message);
            throw;
        }

        try
        {
            // The levels' tiers are paid as they complete (their battle pass XP may move the battle pass again).
            var live = MissionContainers.Live(missions.CurrentValue.Containers);
            foreach (string level in points.Keys.Where(RewardTrackSettings.IsMastery).Where(changed.ContainsKey).ToList())
            {
                var (track, claimed) = await tracks.ClaimAllAsync(playerId, level, CancellationToken.None);
                if (track is null || claimed.Count == 0)
                {
                    continue;
                }

                changed[level] = track;
                var paid = await grants.GrantAsync(playerId, claimed, live, CancellationToken.None);
                foreach (var moved in paid.ChangedTracks)
                {
                    changed[moved["TrackSlug"]!.GetValue<string>()] = moved;
                }
            }

            log.LogInformation("Ranked-set XP for {Player} from {Source} ({Character}, {Result}): {Points}", playerId, source,
                character, won ? "win" : "loss", string.Join(", ", points.Select(p => $"{p.Key} +{p.Value}")));
            // The game shows an XP banner for each track of an XpReward update. As live sent only the battle pass's, the
            // banners are the fighter's level and the battle pass; the account level is still paid, and its state sent
            // with UpdateContext Unknown (no banner).
            var banners = changed.Where(c => c.Key != Account).Select(c => c.Value).ToList();
            if (banners.Count > 0)
            {
                await ProfileNotifications.SendAsync(redis, playerId, ProfileNotifications.RewardTrackStatesUpdated(banners, XpReward));
            }

            if (changed.TryGetValue(Account, out var account))
            {
                await ProfileNotifications.SendAsync(redis, playerId, ProfileNotifications.RewardTrackStatesUpdated([account], Quiet));
            }
        }
        catch (Exception e)
        {
            // The XP is written: a retry would pay it twice, so the record is done.
            log.LogError(e, "Ranked-set XP for {Player} from {Source} paid, but its tiers or the game's update failed: {Error}", playerId, source, e.Message);
        }
    }

    /// <summary>What a ranked set is worth to each track (tracks the hiss does not have are left out).</summary>
    internal static Dictionary<string, int> Points(RewardTrackSettings settings, bool won, string character)
    {
        var points = new Dictionary<string, int>();
        void Add(string? track, int xp)
        {
            if (track is not null && xp > 0 && Hiss.HissTables.Data("milestone-reward-tracks", track) is not null)
            {
                points[track] = xp;
            }
        }

        Add(BattlePass, settings.BattlePassSetXp + (won ? settings.BattlePassWinXp : 0));
        int level = settings.CharacterSetXp + (won ? settings.CharacterWinXp : 0);
        Add(Account, level);
        Add(RewardData.CharacterTrack(character), level);
        return points;
    }

    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue(out string? s) && s.Length > 0 ? s : null;
}

public static class RankedSetXpHosting
{
    /// <summary>End Game's ranked-set XP (<see cref="RankedSetXpPayer"/>), for the match flow service, whose consumer of
    /// match:results hands it the records. Needs AddRewardTracks and AddMissionResults (the mission settings say which
    /// containers' tracks battle pass XP feeds).</summary>
    public static WebApplicationBuilder AddRankedSetXp(this WebApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton<IRankedSetXpPayer, RankedSetXpPayer>();
        return builder;
    }
}

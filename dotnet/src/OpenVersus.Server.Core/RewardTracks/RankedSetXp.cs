using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using OpenVersus.Server.Core.Missions;
using OpenVersus.Server.Core.Realtime;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.RewardTracks;

// End Game's ranked-set XP. The TS server settles ranked sets (and public FFA games) and decides who is paid and whether
// they won (rankedSetXpService.ts: a pregame dodge pays nothing, a concede pays the side that stayed); it publishes
// {playerId, won, character, setKey, source} for each player on reward_tracks:ranked_set (docs/MIGRATION-BRIDGES.md 6).
// Once per player and set:
//
//   - the battle pass (mrt_battlepass_season_five) gains RewardTracks:BattlePassSetXp, +BattlePassWinXp for a win;
//   - the account level (mrt_mastery_account) and the played character's level (its CharacterData MrtSlug, which
//     carries its Fighter Pass) gain CharacterSetXp, +CharacterWinXp;
//   - the account and character levels' newly completed tiers are paid at once, as the Fighter Passes are not claimed
//     by the player (toasts; tiers 5, 10 and 15 pay battle pass XP, which goes to the battle pass by its tag; the last
//     pays the character's Chromium skin);
//   - the game is sent RewardTrackStatesUpdated (UpdateContext XpReward) with the tracks that moved.
//
// Only tracks that are the player's own move (RewardTracks:PerPlayer, CharacterMastery: AddScoreAsync). This is the
// only source of level XP in End Game: RewardTracks:MatchXp and RiftMatchXp are off there, so custom games and rifts
// earn none.
//
// Redis, subscribed  reward_tracks:ranked_set
// Redis, written     ranked_set_xp:{setKey}:{playerId} NX EX 7 days (one payment per player and set)
// Mongo, written     rewardtracks (RewardTrackService), playeritems / playercounters (RewardGrants)
// Published          ws:send RewardTrackStatesUpdated

/// <summary>Pays End Game's ranked-set XP (reward_tracks:ranked_set).</summary>
internal sealed class RankedSetXpSubscriber(IServiceProvider services, IOptionsMonitor<RewardTrackSettings> settings,
    IOptionsMonitor<MissionSettings> missions, IRewardTrackService tracks, IRewardGrants grants, ILogger<RankedSetXpSubscriber> log) : IHostedService
{
    internal const string Channel = "reward_tracks:ranked_set";
    internal const string BattlePass = "mrt_battlepass_season_five";
    internal const string Account = "mrt_mastery_account";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (services.GetService<IConnectionMultiplexer>() is not { } redis)
        {
            return;
        }

        log.LogWarning("MIGRATION BRIDGE: ranked-set XP is paid from the TS server's {Channel} and sent through its websocket (ws:send); see dotnet/docs/MIGRATION-BRIDGES.md (6)", Channel);
        await redis.GetSubscriber().SubscribeAsync(RedisChannel.Literal(Channel), (channel, message) => _ = HandleAsync(message.ToString()));
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal async Task HandleAsync(string message)
    {
        try
        {
            if (JsonNode.Parse(message) is not JsonObject set || Text(set["playerId"]) is not { } playerId || !ObjectId.TryParse(playerId, out _)
                || Text(set["setKey"]) is not { } setKey || services.GetService<IConnectionMultiplexer>()?.GetDatabase() is not { } redis)
            {
                return;
            }

            if (!await redis.StringSetAsync($"ranked_set_xp:{setKey}:{playerId}", "1", TimeSpan.FromDays(7), When.NotExists))
            {
                return;
            }

            bool won = set["won"] is JsonValue w && w.TryGetValue(out bool b) && b;
            string character = Text(set["character"]) ?? "";
            var points = Points(settings.CurrentValue, won, character);
            var changed = (await tracks.AddScoreAsync(playerId, points, CancellationToken.None))
                .ToDictionary(t => t["TrackSlug"]!.GetValue<string>());

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

            log.LogInformation("Ranked-set XP for {Player} from {Source} ({Character}, {Result}): {Points}", playerId, Text(set["source"]) ?? setKey,
                character, won ? "win" : "loss", string.Join(", ", points.Select(p => $"{p.Key} +{p.Value}")));
            if (changed.Count > 0)
            {
                await ProfileNotifications.SendAsync(redis, playerId, ProfileNotifications.RewardTrackStatesUpdated(changed.Values, 6));
            }
        }
        catch (Exception e)
        {
            // Nothing else would ever see it: this runs on the subscription's callback, not a request.
            log.LogError(e, "Ranked-set XP from {Message} not paid: {Error}", message, e.Message);
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
    /// <summary>End Game's ranked-set XP (<see cref="RankedSetXpSubscriber"/>), for the match flow service. Needs
    /// AddRewardTracks and AddMissionResults (the mission settings say which containers' tracks battle pass XP feeds).</summary>
    public static WebApplicationBuilder AddRankedSetXp(this WebApplicationBuilder builder)
    {
        builder.Services.AddHostedService<RankedSetXpSubscriber>();
        return builder;
    }
}

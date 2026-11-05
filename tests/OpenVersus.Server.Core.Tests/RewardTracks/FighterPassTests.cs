using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Hiss;
using OpenVersus.Server.Core.RewardTracks;

namespace OpenVersus.Server.Core.Tests.RewardTracks;

/// <summary>
/// The Fighter Pass extension (FighterPass:ExtraTiers): off, nothing changes; 16 extra tiers make each fighter's tiers
/// 17-32, the old infinity tier a Milestone card and tier 32 the new one, with the track's own rewards repeated under new
/// guids. The game's own table is never changed.
/// </summary>
public sealed class FighterPassTests
{
    private const string Lebron = "mrt_mastery_lebron";
    private static readonly FighterPassSettings s_season2 = new() { ExtraTiers = 16 };

    private static JsonObject GameTracks() => HissTables.Table("milestone-reward-tracks") is var t && FighterPass.Key(FighterPass.Current).Length == 0
        ? t : throw new InvalidOperationException("the extension is on outside FighterPassSwitchTests");

    private static JsonArray Tiers(JsonObject table, string slug) => (JsonArray)table[slug]!["data"]!["Tiers"]!;

    private static int DisplayType(JsonNode? tier) => tier!["DisplayType"]!.GetValue<int>();

    private static double Threshold(JsonNode? tier) => tier!["ScoreThreshold"]!.GetValue<double>();

    private static string?[] Rewards(JsonNode? tier) =>
        [.. tier!["Rewards"]!.AsArray().Select(r => r!["RewardHsda"]?.GetValue<string>() ?? r!["InventoryHsda"]?.GetValue<string>())];

    [Fact]
    public void OffChangesNothing()
    {
        var table = GameTracks();

        Assert.Same(table, FighterPass.Extend(table, new FighterPassSettings()));
        Assert.Equal("", FighterPass.Key(new FighterPassSettings()));
        Assert.Equal(16, Tiers(table, Lebron).Count);
    }

    [Fact]
    public void SixteenExtraTiersMakeTiers17To32WithTheInfinityCardAtTheEnd()
    {
        var game = GameTracks();
        string before = game.ToJsonString();

        var tiers = Tiers(FighterPass.Extend(game, s_season2), Lebron);

        Assert.Equal(32, tiers.Count);
        Assert.Equal(1, DisplayType(tiers[15])); // tier 16, the Chromium skin, a Milestone card now
        Assert.Equal(["skin_ovs_chromium_lebron"], Rewards(tiers[15]));
        Assert.Equal(2, DisplayType(tiers[31]));
        Assert.All(tiers.Skip(16).Take(15), t => Assert.Equal(0, DisplayType(t)));
        Assert.Equal(Enumerable.Range(1, 16).Select(k => 25000d + k * 2500), tiers.Skip(16).Select(Threshold));
        Assert.Equal(before, game.ToJsonString());
    }

    [Fact]
    public void TheExtraTiersRepeatTheTracksRewardsUnderNewGuids()
    {
        var tiers = Tiers(FighterPass.Extend(GameTracks(), s_season2), Lebron);

        // Tiers 17-31 are tiers 1-15 again: Toasts, battle pass XP at 21, 26 and 31; 32 is the reward before the old end.
        for (int k = 0; k < 15; k++)
        {
            Assert.Equal(Rewards(tiers[k]), Rewards(tiers[16 + k]));
        }

        Assert.Equal(["reward_xp_battlepass_700"], Rewards(tiers[31]));
        var tierGuids = tiers.Select(t => t!["TierGuid"]!.GetValue<string>()).ToList();
        var rewardGuids = tiers.SelectMany(t => t!["Rewards"]!.AsArray()).Select(r => r!["RewardGuid"]!.GetValue<string>()).ToList();
        Assert.Equal(tierGuids.Count, tierGuids.Distinct().Count());
        Assert.Equal(rewardGuids.Count, rewardGuids.Distinct().Count());
        Assert.All(tierGuids, g => Assert.Matches("^[0-9A-F]{32}$", g));
        Assert.Equal(tierGuids, Tiers(FighterPass.Extend(GameTracks(), s_season2), Lebron).Select(t => t!["TierGuid"]!.GetValue<string>()));
    }

    [Fact]
    public void TheFinalRewardNamesTheFighter()
    {
        var tiers = Tiers(FighterPass.Extend(GameTracks(), new FighterPassSettings { ExtraTiers = 16, FinalReward = "skin_ovs_gold_{fighter}" }), Lebron);

        var reward = tiers[31]!["Rewards"]!.AsArray().Single()!;
        Assert.Equal("skin_ovs_gold_lebron", reward["InventoryHsda"]!.GetValue<string>());
        Assert.Equal("DirectInventoryItem", reward["RewardGrantMethod"]!.GetValue<string>());
    }

    [Fact]
    public void OnlyFighterTracksEndingInTheInfinityCardAreExtended()
    {
        var game = GameTracks();
        var extended = FighterPass.Extend(game, s_season2);

        foreach (var (slug, entry) in game)
        {
            int had = Tiers(game, slug).Count;
            bool fighter = FighterPass.Extends(slug, entry!["data"]!.AsObject());
            Assert.Equal(fighter ? had + 16 : had, Tiers(extended, slug).Count);
            if (fighter)
            {
                Assert.Equal(1, Tiers(extended, slug).Count(t => DisplayType(t) == 2));
            }
        }

        // 35 fighters; mrt_mastery_c020b, an old 14-tier track with no infinity tier and no Chromium skin, is left alone.
        Assert.Equal(35, game.Count(e => FighterPass.Extends(e.Key, e.Value!["data"]!.AsObject())));
        Assert.Equal(Tiers(game, "mrt_mastery_account").Count, Tiers(extended, "mrt_mastery_account").Count);
        Assert.Equal(Tiers(game, "mrt_battlepass_season_five").Count, Tiers(extended, "mrt_battlepass_season_five").Count);
    }

    [Fact]
    public void TheHissAnswerIsExtendedAsTheTablesAre()
    {
        var answer = HissService.Fill(HissService.Values(1, 2, []));

        HissService.ExtendFighterPasses(answer, s_season2);

        var tracks = answer["body"]!["Data"]!["milestone-reward-tracks"]!["_hydra_compressed"]!.AsObject();
        Assert.Equal(Tiers(FighterPass.Extend(GameTracks(), s_season2), Lebron).ToJsonString(), Tiers(tracks, Lebron).ToJsonString());
    }
}

/// <summary>
/// The extension switched on for the process: the server's own tables (the score cap, the tiers reached) and the
/// answer's infinity index follow it. Run alone, as it changes what every other test reads.
/// </summary>
[Collection(nameof(FighterPassSwitch))]
public sealed class FighterPassSwitchTests
{
    private const string Lebron = "mrt_mastery_lebron";

    [Fact]
    public void OnTheCapIsTheNewEndAndAFinishedFighterGoesOnTowardsTier17()
    {
        try
        {
            Assert.Equal(25000, RewardTrackService.Capped(Lebron, long.MaxValue));
            Assert.Null(FighterPass.InfiniteTierIndex(Lebron));

            FighterPass.Use(new FighterPassSettings { ExtraTiers = 16 });

            Assert.Equal(65000, RewardTrackService.Capped(Lebron, long.MaxValue));
            Assert.Equal(31, FighterPass.InfiniteTierIndex(Lebron));
            Assert.Null(FighterPass.InfiniteTierIndex("mrt_mastery_account"));
            var atOldEnd = RewardTrackService.Scored(Lebron, RewardTrackService.Initial(Lebron), 25000);
            Assert.Equal(16, atOldEnd["CompletedTiers"]!.AsArray().Count);
            Assert.Equal(16, atOldEnd["CurrentTier"]!.GetValue<int>()); // working towards tier 17
            var done = RewardTrackService.Scored(Lebron, RewardTrackService.Initial(Lebron), 65000);
            Assert.Equal(31, done["CurrentTier"]!.GetValue<int>());
        }
        finally
        {
            FighterPass.Use(new FighterPassSettings());
        }

        Assert.Equal(25000, RewardTrackService.Capped(Lebron, long.MaxValue));
    }
}

[CollectionDefinition(nameof(FighterPassSwitch), DisableParallelization = true)]
public sealed class FighterPassSwitch;

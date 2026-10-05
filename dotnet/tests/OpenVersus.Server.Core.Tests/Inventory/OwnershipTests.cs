using System.Text.Json.Nodes;
using MongoDB.Bson;
using OpenVersus.Server.Core.Access;
using OpenVersus.Server.Core.Hiss;
using OpenVersus.Server.Core.Inventory;

namespace OpenVersus.Server.Core.Tests.Inventory;

/// <summary>
/// End Game's restricted items (Ownership): the battle pass and Fighter Pass rewards and the OVS Dev badge are nobody's
/// by default; the dev accounts own the badge and get its OVSDev stat. Needs no stores.
/// </summary>
public sealed class OwnershipTests
{
    [Fact]
    public void BattlePassFighterPassAndTheBadgeAreRestricted()
    {
        Assert.True(Ownership.IsRestricted("skin_ovs_painter_beetlejuice"));     // the battle pass's tier 48
        Assert.True(Ownership.IsRestricted("skin_ovs_chromium_shaggy"));         // a Fighter Pass's last tier
        Assert.True(Ownership.IsRestricted("stat_tracking_bundle_ovs_dev"));
        Assert.True(Ownership.IsDevBadge("stat_tracking_bundle_ovs_dev"));
        Assert.False(Ownership.IsRestricted("skin_shaggy_default"));
        Assert.True(Ownership.IsRestricted("taunt_bananaguard_onetoughbanana"));  // the battle pass's tier 3
        Assert.False(Ownership.IsRestricted("taunt_bananaguard_mamasaid"));
        Assert.False(Ownership.IsRestricted(null));
    }

    [Fact]
    public void DevAccountsAreTheListedIds()
    {
        var settings = new OwnershipSettings { OvsDevAccountIds = " a1, b2 ,," };
        Assert.True(Ownership.IsDevAccount(settings, "a1"));
        Assert.True(Ownership.IsDevAccount(settings, "b2"));
        Assert.False(Ownership.IsDevAccount(settings, "c3"));
        Assert.False(Ownership.IsDevAccount(settings, ""));
        Assert.False(Ownership.IsDevAccount(new OwnershipSettings(), "a1"));
    }

    [Fact]
    public void TheHissOwnsByDefaultEverythingButTheRestricted()
    {
        static BsonDocument Asset(string type, string slug) => new() { ["assetType"] = type, ["enabled"] = true, ["slug"] = slug };
        var values = HissService.Values(1, [Asset("SkinData", "skin_shaggy_default"), Asset("SkinData", "skin_ovs_chromium_shaggy"),
            Asset("StatTrackingBundleData", "stat_tracking_bundle_ovs_dev"), Asset("SkinData", "skin_ovs_painter_beetlejuice")]);
        Assert.Equal(["skin_shaggy_default"], values["{{assets:all}}"]!.AsArray().Select(s => s!.GetValue<string>()));
        // The lists by type still name them: they exist, nobody owns them by default.
        Assert.Contains("stat_tracking_bundle_ovs_dev", values["{{assets:StatTrackingBundleData}}"]!.AsArray().Select(s => s!.GetValue<string>()));
    }

    [Fact]
    public void OnlyDevAccountsGetTheOvsDevStat()
    {
        var trackers = new JsonObject { ["season5"] = new JsonObject { ["ranked"] = new JsonObject { ["1v1"] = new JsonObject() } } };
        StatTrackers.From(null, null).WriteTo(trackers);
        Assert.Null(trackers["OVSDev"]);
        StatTrackers.From(null, null, ovsDev: true).WriteTo(trackers);
        Assert.Equal(1, trackers["OVSDev"]!.GetValue<int>());
    }
}

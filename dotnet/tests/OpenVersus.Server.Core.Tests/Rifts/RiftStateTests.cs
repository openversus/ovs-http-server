using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Rifts;

namespace OpenVersus.Server.Core.Tests.Rifts;

public sealed class RiftStateTests
{
    [Fact]
    // The game's handler reads body.RiftState; a bare state as the body left the rift page waiting forever.
    public void TheAnswerHoldsTheStateUnderRiftState()
    {
        var state = RiftStateService.NewState(new RiftSettings());

        var answer = RiftStateService.Answer(state);

        Assert.Same(state, answer["body"]!["RiftState"]);
        Assert.Single(answer["body"]!.AsObject());
        Assert.Equal(0, (int)answer["return_code"]!);
    }

    [Fact]
    public void ANewPlayerHasOnlyTheGlobalPoolFullAndNothingPlayed()
    {
        var state = RiftStateService.NewState(new RiftSettings());

        var global = state["PlayerAttrition"]!["GlobalAttrition"]!;
        Assert.Equal(6, (int)global["CurrentAttritionStocks"]!);
        Assert.Equal(5, (int)global["CurrentDailyAttempts"]!);
        Assert.Equal(0, (int)global["CurrentAttritionDamage"]!);
        Assert.Single(state["PlayerAttrition"]!.AsObject());
        Assert.Equal("", (string)state["LastPlayed"]!["RiftSlug"]!);
        Assert.Empty(state["DailyRewards"]!["Rifts"]!.AsArray());
    }

    [Fact]
    public void TheGlobalPoolComesFromTheSettings()
    {
        var global = RiftStateService.NewState(new RiftSettings { GlobalAttritionStocks = 9, GlobalDailyAttempts = 2 })["PlayerAttrition"]!["GlobalAttrition"]!;

        Assert.Equal(9, (int)global["CurrentAttritionStocks"]!);
        Assert.Equal(2, (int)global["CurrentDailyAttempts"]!);
    }

    [Fact]
    public void TheStateHasTheKeysTheGameCachedFromWb()
    {
        var state = RiftStateService.NewState(new RiftSettings());

        Assert.Equal(["DailyRewards", "LastPlayed", "PlayerAttrition"], state.Select(p => p.Key));
        Assert.Equal(["Chapter", "Chapters", "CompletedNodes", "RiftSlug", "Rifts"], state["DailyRewards"]!.AsObject().Select(p => p.Key));
        Assert.Equal(["Chapter", "NodeId", "RiftSlug"], state["LastPlayed"]!.AsObject().Select(p => p.Key));
        Assert.Equal(["CurrentAttritionDamage", "CurrentAttritionRegenTimestamp", "CurrentAttritionStocks", "CurrentDailyAttempts", "CurrentResetAttempts"],
            state["PlayerAttrition"]!["GlobalAttrition"]!.AsObject().Select(p => p.Key));
    }
}

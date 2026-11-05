using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Rifts;

namespace OpenVersus.Server.Core.Tests.Rifts;

/// <summary>A rift node's stars, judged from a real match: the Joker node at Easy, won with Wonder Woman (skin_c001_s01)
/// on the bench (joker-node-win-counters.json: the winner's counters as the game reported them, two ring-outs, no fully
/// charged hit).</summary>
public sealed class RiftMissionTests
{
    private const string Rift = "mvs_joker_rift";
    private const string Node = "EF645A6A459DC8579E8856BAA6A09C82";

    private static JsonObject Counters() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Rifts", "joker-node-win-counters.json")))!.AsObject();

    [Fact]
    // The five the game shows at Easy, from the hiss (load_rifts lists another third mission).
    public void TheNodeOffersTheFiveMissionsTheGameShows() =>
        Assert.Equal(["mis_win_rift_node", "mis_rift_ringout", "mis_rift_total_fully_charged_attacks_hit_once", "mis_win_rift_node_with_dc_character", "mis_play_coop"],
            RiftMissions.NodeMissions(Rift, Node, 0));

    [Fact]
    public void TheWinEarnsWinRingoutAndDcButNotChargedOrCoop()
    {
        var unknown = new List<string>();

        var earned = RiftMissions.Earned(RiftMissions.NodeMissions(Rift, Node, 0),
            new RiftMissionContext(true, "character_wonder_woman", "skin_c001_s01", Counters()), unknown);

        Assert.Equal(["mis_win_rift_node", "mis_rift_ringout", "mis_win_rift_node_with_dc_character"], earned);
        Assert.Empty(unknown);
    }

    [Fact]
    // The same match with a character from outside DC: no DC star. A counter at 0 earns nothing.
    public void ANonDcSkinOrAZeroCounterEarnsNothingForThem()
    {
        var counters = Counters();
        counters["Stat:Game:Character:TotalRingouts"] = 0;

        var earned = RiftMissions.Earned(RiftMissions.NodeMissions(Rift, Node, 0),
            new RiftMissionContext(true, "character_shaggy", "skin_shaggy_default", counters), []);

        Assert.Equal(["mis_win_rift_node"], earned);
    }

    [Fact]
    // A counter reported as a Hydra double ({"_hydra_double": n}) counts as n.
    public void AHydraDoubleCounterCounts()
    {
        var counters = Counters();
        counters["Stat:Game:Character:TotalFullyChargedAttacksHit"] = new JsonObject { ["_hydra_double"] = 1.0 };

        var earned = RiftMissions.Earned(["mis_rift_total_fully_charged_attacks_hit_once"],
            new RiftMissionContext(true, "character_wonder_woman", "skin_c001_s01", counters), []);

        Assert.Equal(["mis_rift_total_fully_charged_attacks_hit_once"], earned);
    }

    [Fact]
    // Stars are kept per difficulty, each once, and the chapter's cauldron score counts the new ones (as in the WB-era
    // cache, where the score equals the missions completed at that difficulty).
    public void StarsAreKeptOnceAndCountedInTheCauldron()
    {
        var player = RiftProgressService.NewPlayer();
        const string chapter = "164A492D440EA7BFC8A6C9AF50F96A49";

        var first = RiftProgressService.RecordStars(player, Rift, chapter, Node, 0, ["mis_win_rift_node", "mis_rift_ringout"]);
        var second = RiftProgressService.RecordStars(player, Rift, chapter, Node, 0, ["mis_win_rift_node", "mis_win_rift_node_with_dc_character"]);

        Assert.Equal(["mis_win_rift_node", "mis_rift_ringout"], first);
        Assert.Equal(["mis_win_rift_node_with_dc_character"], second);
        Assert.Equal(["mis_win_rift_node", "mis_rift_ringout", "mis_win_rift_node_with_dc_character"],
            player[Rift]!["RuntimeNodeData"]![Node]!["CompletedMissions"]!["0"]!.AsArray().Select(n => (string?)n));
        Assert.Equal(3, (int)player[Rift]!["RuntimeChapterData"]![chapter]!["CauldronsByDifficulty"]![0]!["CurrentScore"]!);
        Assert.Equal(0, (int)player[Rift]!["RuntimeChapterData"]![chapter]!["CauldronsByDifficulty"]![1]!["CurrentScore"]!);
    }
}

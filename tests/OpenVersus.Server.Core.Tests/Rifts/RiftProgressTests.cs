using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Hydra;
using OpenVersus.Server.Core.Rifts;
using OpenVersus.Server.Core.Static;

namespace OpenVersus.Server.Core.Tests.Rifts;

/// <summary>Each player's rift runtime data, and a win recorded in it.</summary>
public sealed class RiftProgressTests
{
    private const string Chapter = "164A492D440EA7BFC8A6C9AF50F96A49";
    private const string Node = "EF645A6A459DC8579E8856BAA6A09C82";

    private static IEnumerable<JsonObject> Chapters(JsonObject dynamic) =>
        dynamic.Select(r => r.Value?["RuntimeChapterData"] as JsonObject).OfType<JsonObject>().SelectMany(c => c.Select(e => e.Value)).OfType<JsonObject>();

    [Fact]
    // The frozen copy holds one account's finished tutorial nodes; a new player starts from scratch, keeping the bots.
    public void ANewPlayerStartsFromScratchWithTheFrozenBots()
    {
        var frozen = JsonNode.Parse(StaticResponses.Json("ssc-load-rifts"))!["body"]!["DynamicInstanceRuntimeData"]!.AsObject();
        Assert.Contains(Chapters(frozen), c => c["NodeCompletionsByDifficulty"] is JsonObject done && done.Count > 0);

        var fresh = RiftProgressService.NewDynamic();

        Assert.All(Chapters(fresh), c =>
        {
            Assert.Empty(c["NodeCompletionsByDifficulty"]!.AsObject());
            Assert.False((bool)c["bIsChapterComplete"]!);
            Assert.Equal(0, (int)c["HighestDifficultyCompleted"]!);
        });
        Assert.True(JsonNode.DeepEquals(frozen["mvs_joker_rift"]!["RuntimeNodeData"], fresh["mvs_joker_rift"]!["RuntimeNodeData"]));
    }

    [Fact]
    // As the WB-era cache holds it: {"<difficulty>": [node, ...]}, each node once.
    public void AWinIsRecordedOncePerDifficulty()
    {
        var dynamic = RiftProgressService.NewDynamic();

        RiftProgressService.RecordWin(dynamic, "mvs_joker_rift", Chapter, Node, 0);
        RiftProgressService.RecordWin(dynamic, "mvs_joker_rift", Chapter, Node, 0);
        RiftProgressService.RecordWin(dynamic, "mvs_joker_rift", Chapter, Node, 1);

        var chapter = dynamic["mvs_joker_rift"]!["RuntimeChapterData"]![Chapter]!;
        Assert.Equal([Node], chapter["NodeCompletionsByDifficulty"]!["0"]!.AsArray().Select(n => (string?)n));
        Assert.Equal([Node], chapter["NodeCompletionsByDifficulty"]!["1"]!.AsArray().Select(n => (string?)n));
        Assert.Equal(1, (int)chapter["CurrentDifficulty"]!);
        Assert.False((bool)chapter["bIsChapterComplete"]!);
    }

    [Fact]
    // What the lobby and the match read after a win: the player's own copy, not the frozen one.
    public void TheLobbysRuntimeDataIsThePlayersOwn()
    {
        var dynamic = RiftProgressService.NewDynamic();
        RiftProgressService.RecordWin(dynamic, "mvs_joker_rift", Chapter, Node, 0);

        var data = RiftLobbyService.RuntimeData(dynamic, "mvs_joker_rift");

        Assert.Equal(Node, (string?)data["RuntimeChapterData"]![Chapter]!["NodeCompletionsByDifficulty"]!["0"]![0]);
        Assert.Empty(data["Powerups"]!.AsArray());
    }

    [Fact]
    // load_rifts splices the configurations' Hydra, encoded once, into each answer: it must be the whole answer's bytes.
    public void TheSplicedHydraIsTheWholeAnswersBytes()
    {
        var configs = JsonNode.Parse(StaticResponses.Json("ssc-load-rifts"))!["body"]!["RiftConfigs"]!;
        var dynamic = RiftProgressService.NewDynamic();
        var player = RiftProgressService.NewPlayer();
        JsonObject Answer(JsonNode riftConfigs) => new()
        {
            ["body"] = new JsonObject
            {
                ["RiftConfigs"] = riftConfigs,
                ["DynamicInstanceRuntimeData"] = dynamic.DeepClone(),
                ["PlayerInstanceRuntimeData"] = player.DeepClone(),
            },
            ["metadata"] = null,
            ["return_code"] = 0,
        };

        Assert.Equal(HydraEncoder.Encode(Answer(configs.DeepClone())), HydraEncoder.Encode(Answer(HydraRaw.Node(HydraEncoder.Encode(configs)))));
    }
}

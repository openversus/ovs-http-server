using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Rifts;
using OpenVersus.Server.Core.Static;

namespace OpenVersus.Server.Core.Tests.Rifts;

/// <summary>The rifts offered: the TS server's, the hiss-only Season 6 rogue rifts added, no rift ending.</summary>
public sealed class RiftCatalogTests
{
    private static readonly string[] s_season6 = ["horror_rogue_rift_1", "heroic_rogue_rift1", "discipline_rogue_rift_1", "chaos_rogue_rift1"];

    private static JsonObject Frozen() => JsonNode.Parse(StaticResponses.Json("ssc-load-rifts"))!["body"]!.AsObject();

    [Theory]
    [InlineData("horror_rogue_rift")]
    [InlineData("heroic_rogue_rift")]
    [InlineData("discipline_rogue_rift1")]
    [InlineData("rift_s5_challenge_chaos")]
    // The control for the generated rifts: from a Season 5 rogue rift's config, the generator makes exactly the runtime
    // data WB's server made for it (the frozen copy, untouched). The one freedom: a bot whose CharacterSet WB was seen
    // to fill several ways (character_set_everyone_s5, a random pool) may be any of those.
    public void TheGeneratorReproducesASeason5RogueRiftsRuntimeData(string slug)
    {
        var frozen = Frozen();
        var config = frozen["RiftConfigs"]!.AsArray().OfType<JsonObject>().Single(c => (string?)c["slug"] == slug);
        var picks = RiftCatalog.ObservedPicks(frozen["RiftConfigs"]!.AsArray(), frozen["DynamicInstanceRuntimeData"]!.AsObject());

        var (dynamic, player) = RiftCatalog.NewRuntime(config, picks);

        int pooled = 0;
        foreach (var (nodeId, node) in dynamic["RuntimeNodeData"]!.AsObject())
        {
            var wb = frozen["DynamicInstanceRuntimeData"]![slug]!["RuntimeNodeData"]![nodeId]!;
            var teams = node!["EnemyTeams"]!.AsArray().Select((t, i) => (t!, wb["EnemyTeams"]![i]!, config["RiftMatchNodeData"]![nodeId]!["MatchData"]!["EnemyTeams"]![i]!))
                .Append((node["FriendlyTeam"]!, wb["FriendlyTeam"]!, config["RiftMatchNodeData"]![nodeId]!["MatchData"]!["FriendlyTeam"]!));
            foreach (var (ours, theirs, team) in teams)
            {
                for (int i = 0; i < ours["BotLoadouts"]!.AsArray().Count; i++)
                {
                    var (mine, wbs) = (ours["BotLoadouts"]![i]!, theirs["BotLoadouts"]![i]!);
                    if (JsonNode.DeepEquals(mine, wbs))
                    {
                        continue;
                    }

                    var options = picks[(string)team["Bots"]![i]!["CharacterSet"]!];
                    Assert.True(options.Count > 1, $"{nodeId} bot {i}: a single-pick set generated differently");
                    Assert.Contains(((string)mine["Character"]!, (string)mine["Skin"]!), options);
                    Assert.Contains(((string)wbs["Character"]!, (string)wbs["Skin"]!), options);
                    mine["Character"] = (string)wbs["Character"]!;
                    mine["Skin"] = (string)wbs["Skin"]!;
                    pooled++;
                }
            }
        }

        Assert.True(JsonNode.DeepEquals(frozen["DynamicInstanceRuntimeData"]![slug], dynamic), $"dynamic: {dynamic.ToJsonString()}");
        Assert.True(JsonNode.DeepEquals(frozen["PlayerInstanceRuntimeData"]![slug], player), $"player: {player.ToJsonString()}");
    }

    [Fact]
    public void TheSeason6RogueRiftsAreOfferedWithBotsOnEveryMatchNode()
    {
        var slugs = RiftCatalog.Configs.OfType<JsonObject>().Select(c => (string?)c["slug"]).ToList();

        Assert.All(s_season6, s => Assert.Contains(s, slugs));
        foreach (string slug in s_season6)
        {
            var nodes = RiftCatalog.Dynamic[slug]!["RuntimeNodeData"]!.AsObject();
            Assert.NotEmpty(nodes);
            Assert.All(nodes, n => Assert.NotEmpty(n.Value!["EnemyTeams"]![0]!["BotLoadouts"]!.AsArray()));
            Assert.Equal(nodes.Select(n => n.Key), RiftCatalog.Player[slug]!["RuntimeNodeData"]!.AsObject().Select(n => n.Key));
        }
    }

    [Fact]
    // Every rift that had an end time keeps it, EndTimeYears (20) later; the TS answer's 2025 dates are gone.
    public void NoOfferedRiftHasEnded()
    {
        var ending = RiftCatalog.Configs.OfType<JsonObject>().Select(c => c["RiftData"]!).Where(d => (bool?)d["bRiftHasEndTime"] == true).ToList();

        Assert.Equal(9, ending.Count);
        Assert.All(ending, d => Assert.True((int)d["RiftEndTime"]!["Year"]! >= 2045));
    }

    [Fact]
    public void TheHissRiftConfigsEndTimesMoveToo()
    {
        var section = new JsonObject
        {
            ["heroic_rogue_rift"] = new JsonObject { ["data"] = new JsonObject { ["RiftData"] = new JsonObject
            {
                ["bRiftHasEndTime"] = true,
                ["RiftEndTime"] = new JsonObject { ["Day"] = 18, ["Month"] = 2, ["Year"] = 2025 },
            } } },
            ["mvs_joker_rift"] = new JsonObject { ["data"] = new JsonObject { ["RiftData"] = new JsonObject { ["bRiftHasEndTime"] = false } } },
        };

        RiftCatalog.ExtendEndTimes(section);

        Assert.Equal(2025 + RiftCatalog.EndTimeYears, (int)section["heroic_rogue_rift"]!["data"]!["RiftData"]!["RiftEndTime"]!["Year"]!);
        Assert.Equal(18, (int)section["heroic_rogue_rift"]!["data"]!["RiftData"]!["RiftEndTime"]!["Day"]!);
        Assert.Null(section["mvs_joker_rift"]!["data"]!["RiftData"]!["RiftEndTime"]);
    }

    [Fact]
    // A player stored before the Season 6 rifts were added gets them as a new player's copy; their progress stays.
    public void AStoredPlayerGetsTheAddedRiftsAndKeepsTheirProgress()
    {
        var stored = RiftProgressService.NewDynamic();
        foreach (string slug in s_season6)
        {
            stored.Remove(slug);
        }

        RiftProgressService.RecordWin(stored, "mvs_joker_rift", "164A492D440EA7BFC8A6C9AF50F96A49", "EF645A6A459DC8579E8856BAA6A09C82", 0);

        RiftProgressService.AddMissingRifts(stored, RiftProgressService.NewDynamic());

        Assert.All(s_season6, s => Assert.True(stored.ContainsKey(s)));
        Assert.NotEmpty(stored["mvs_joker_rift"]!["RuntimeChapterData"]!["164A492D440EA7BFC8A6C9AF50F96A49"]!["NodeCompletionsByDifficulty"]!.AsObject());
    }
}

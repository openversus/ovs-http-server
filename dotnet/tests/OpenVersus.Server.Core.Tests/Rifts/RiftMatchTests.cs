using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Matches;
using OpenVersus.Server.Core.Rifts;
using OpenVersus.Server.Core.Static;

namespace OpenVersus.Server.Core.Tests.Rifts;

/// <summary>A rift node's match, built from the load_rifts data the game has, as the client's offline creator builds it.</summary>
public sealed class RiftMatchTests
{
    private const string Human = "0000000000000000000a0009";
    private static readonly RiftMatchService.RiftHuman s_human = new(Human, "203.0.113.7", "character_wonder_woman", "skin_c001_s01");

    private static readonly JsonObject s_rifts = JsonNode.Parse(StaticResponses.Json("ssc-load-rifts"))!["body"]!.AsObject();

    private static MatchLaunch Build(string slug, string node, int difficulty = 0, int? poolStocks = null)
    {
        var config = s_rifts["RiftConfigs"]!.AsArray().OfType<JsonObject>().Single(c => (string?)c["slug"] == slug);
        var matchData = config["RiftMatchNodeData"]![node]!["MatchData"]!.AsObject();
        var runtimeNode = RiftLobbyService.RuntimeData(slug)["RuntimeNodeData"]![node] as JsonObject;
        return RiftMatchService.Build(s_human, node, matchData, runtimeNode, difficulty, poolStocks);
    }

    private static JsonObject Config(MatchLaunch launch) => launch.GameplayConfigOverride!;

    private static int TargetScore(MatchLaunch launch, int team) => (int)Config(launch)["TeamData"]![team]!["TargetScore"]!;

    [Fact]
    // The Joker rift node played on the bench: one enemy bot (Joker, 2 stocks, a team buff) against the player's 3.
    public void AOneBotNodeIsARift1v1WithEachSidesStocks()
    {
        var launch = Build("mvs_joker_rift", "EF645A6A459DC8579E8856BAA6A09C82");

        Assert.Equal([(Human, 0, 0, false), ("Bot0", 1, 1, true)],
            launch.Players.Select(p => (p.PlayerId, p.PlayerIndex, p.TeamIndex, p.IsBot)));
        Assert.True(launch.Players[0].IsHost);
        Assert.Equal("203.0.113.7", launch.Players[0].Ip);
        Assert.Equal("1v1", launch.Mode);
        Assert.Equal("M001", launch.Map);

        var config = Config(launch);
        Assert.True((bool)config["bIsRift"]!);
        Assert.False((bool)config["bIsPvP"]!);
        Assert.Equal("TargetScoreIsLoss", (string?)config["ScoreEvaluationRule"]);
        Assert.Equal("AttributeToVictim", (string?)config["ScoreAttributionRule"]);
        Assert.Equal("EF645A6A459DC8579E8856BAA6A09C82", (string?)config["RiftNodeId"]);
        Assert.Equal("Attunements:SeasonOne:Chaos", (string?)config["RiftNodeAttunement"]);
        Assert.Equal("1v1", (string?)config["ModeString"]);
        Assert.Equal(240, (int)config["MatchDurationSeconds"]!);
        Assert.True((bool)config["bAllowMapHazards"]!);
        Assert.Equal(3, TargetScore(launch, 0));
        Assert.Equal(2, TargetScore(launch, 1));

        var bot = launch.PlayerConfigOverrides!["Bot0"]!;
        Assert.Equal("character_C028", (string?)bot["Character"]);
        Assert.Equal("skin_c028_default", (string?)bot["Skin"]);
        Assert.Equal("banner_jokerhaha", (string?)bot["Banner"]);
        Assert.Equal(["buff_difficulty_reduction_small"], bot["Buffs"]!.AsArray().Select(b => (string?)b));
        Assert.Empty(bot["Perks"]!.AsArray());

        var human = launch.PlayerConfigOverrides[Human]!;
        Assert.Equal("character_wonder_woman", (string?)human["Character"]);
        Assert.Equal("skin_c001_s01", (string?)human["Skin"]);
    }

    [Fact]
    // A partner bot makes team 0 two players: "2v2", and the partner after the human (PlayerIndex 2), the enemies at 1, 3.
    public void APartnerBotMakesIt2v2WithHumansAtTheLowestIndexes()
    {
        var launch = Build("rift_s2_mojo_jojo", "55ABE7374719AD7F75BBA9977ACF3ECF");

        Assert.Equal([(Human, 0, 0), ("Bot2", 2, 0), ("Bot0", 1, 1), ("Bot1", 3, 1)],
            launch.Players.Select(p => (p.PlayerId, p.PlayerIndex, p.TeamIndex)));
        Assert.Equal("2v2", (string?)Config(launch)["ModeString"]);
        Assert.Equal("character_c024", (string?)launch.PlayerConfigOverrides!["Bot2"]!["Character"]);
    }

    [Theory]
    [InlineData(0, 300)]
    // Beyond the list: its last entry, as the client indexes it.
    [InlineData(3, 300)]
    public void TheDurationIsTheDifficultysEntry(int difficulty, int seconds) =>
        Assert.Equal(seconds, (int)Config(Build("rift_s2_mojo_jojo", "55ABE7374719AD7F75BBA9977ACF3ECF", difficulty))["MatchDurationSeconds"]!);

    [Fact]
    // Where the chapter carries player stocks over, team 0 plays with the attrition pool, not the node's stocks.
    public void ThePoolReplacesTheNodesStocksWhenGiven() =>
        Assert.Equal(5, TargetScore(Build("mvs_joker_rift", "EF645A6A459DC8579E8856BAA6A09C82", poolStocks: 5), 0));

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    // Beyond the list: its last entry.
    [InlineData(7, true)]
    public void TheCarryOverIsReadAtTheDifficulty(int difficulty, bool carries)
    {
        var attrition = new JsonObject
        {
            ["DifficultyModifiedSettings"] = new JsonArray(
                new JsonObject { ["bDoPlayerStocksAndDamageCarryOver"] = false },
                new JsonObject { ["bDoPlayerStocksAndDamageCarryOver"] = true }),
        };

        Assert.Equal(carries, RiftMatchService.CarriesPlayerStocksOver(attrition, difficulty));
    }

    [Fact]
    // The Joker chapter never carries stocks over, so the bench match must not use the pool (6 stocks).
    public void TheJokerChapterDoesNotCarryStocksOver()
    {
        var config = s_rifts["RiftConfigs"]!.AsArray().OfType<JsonObject>().Single(c => (string?)c["slug"] == "mvs_joker_rift");
        var attrition = config["RiftChapterData"]!["164A492D440EA7BFC8A6C9AF50F96A49"]!["Attrition"] as JsonObject;

        Assert.False(RiftMatchService.CarriesPlayerStocksOver(attrition, 0));
    }
}

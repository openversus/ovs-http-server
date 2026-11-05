using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using OpenVersus.Server.Core.Matches;

namespace OpenVersus.Server.Core.Tests.Matches;

/// <summary>
/// How MatchHistoryService builds a page from a stats document. The answers themselves are compared with the TS
/// server's by tools/matches/matches_diff.mjs (every stored account); these pin the rules that data rarely reaches.
/// </summary>
public sealed class MatchHistoryPageTests
{
    private const string Me = "0000000000000000000a0001", Them = "0000000000000000000a0002", Mate = "0000000000000000000a0003", Other = "0000000000000000000a0004";
    private static readonly Dictionary<string, string> s_names = new() { [Me] = "me", [Them] = "them", [Mate] = "mate" };
    private static readonly DateTimeOffset s_now = DateTimeOffset.FromUnixTimeMilliseconds(1790000000000);

    private static BsonDocument Player(string id, string character, int team, bool winner) => new()
    {
        { "accountId", id }, { "character", character }, { "teamIndex", team }, { "damage", 120.0 }, { "ringouts", 3 }, { "deaths", 1 }, { "isWinner", winner },
    };

    // A 1v1 as the stats writer stores it: I am on team 1 here, so "team 0 won" and "I won" differ.
    private static BsonDocument Entry(string matchId, BsonValue timestamp, string mode = "1v1", string result = "win", BsonArray? players = null) => new()
    {
        { "matchId", matchId }, { "timestamp", timestamp }, { "mode", mode }, { "map", "M001" }, { "result", result },
        { "score", new BsonArray { 3, 1 } },
        { "players", players ?? new BsonArray { Player(Them, "unknown", 0, result != "win"), Player(Me, "character_shaggy", 1, result == "win") } },
    };

    private static JsonObject Page(BsonDocument? stats, string? count = "10", string? page = "1") =>
        MatchHistoryService.Page(Me, stats, count, page, s_names, s_now, new Random(1), NullLogger.Instance);

    private static string[] Ids(JsonObject page) => page["matches"]!.AsArray().Select(m => (string)m!["id"]!).ToArray();

    [Fact]
    public void NoStatsIsOneEmptyPage()
    {
        var page = Page(null);
        Assert.Empty(page["matches"]!.AsArray());
        Assert.Equal(0, (int)page["total_matches"]!);
        Assert.Equal(1.0, (double)page["total_pages"]!);
    }

    [Fact]
    public void BothModesOldestFirstAndPaged()
    {
        var stats = new BsonDocument
        {
            { "recent_matches_1v1", new BsonArray { Entry("a", 300L), Entry("b", 100L) } },
            { "recent_matches_2v2", new BsonArray { Entry("c", 200L, mode: "2v2"), Entry("d", 400L, mode: "FFA") } },
        };
        Assert.Equal(["b", "c", "a", "d"], Ids(Page(stats)));
        var second = Page(stats, count: "3", page: "2");
        Assert.Equal(["d"], Ids(second));
        Assert.Equal(2.0, (double)second["total_pages"]!);
        Assert.Equal("2v2_container", (string)Page(stats)["matches"]![1]!["name"]!);
    }

    [Fact]
    public void AMatchThatCannotBeBuiltIsLeftOut()
    {
        var stats = new BsonDocument { { "recent_matches_1v1", new BsonArray { Entry("good", 100L), Entry("bad", "yesterday") } } };
        var page = Page(stats);
        Assert.Equal(["good"], Ids(page));
        Assert.Equal(2, (int)page["total_matches"]!);
    }

    private static JsonNode OnlyMatch(BsonDocument entry, string list = "recent_matches_1v1") =>
        Page(new BsonDocument { { list, new BsonArray { entry } } })["matches"]![0]!;

    private static string[] Strings(JsonNode? array) => array!.AsArray().Select(n => (string)n!).ToArray();

    [Fact]
    public void AMatchAsTheTsServerBuildsIt()
    {
        var match = OnlyMatch(Entry("m1", 1790000000123L, result: "loss"));
        Assert.Equal("2026-09-21T14:13:20.123Z", (string)match["arbitration"]!["end_time"]!);
        Assert.Equal(1790000000L, (long)match["completion_time"]!["_hydra_unix_date"]!);
        var players = match["server_data"]!["GameplayConfig"]!["Players"]!;
        Assert.Equal("skin_shaggy_default", (string)players[Me]!["Skin"]!);
        Assert.Equal("skin_unknown_default", (string)players[Them]!["Skin"]!);
        Assert.Equal(120.0, (double)match["players"]!["all"]![0]!["data"]!["EndOfMatchStats"]!["PlayerMissionUpdates"]![Me]!["Stat:Game:Character:TotalDamageDealt"]!);
    }

    [Fact]
    public void TheWinnersAndTheirTeamComeFromThePlayers()
    {
        // I won on team 1: the TS server said team 0.
        var match = OnlyMatch(Entry("m1", 100L, result: "win"));
        Assert.Equal([Me], Strings(match["win"]));
        Assert.Equal([Them], Strings(match["loss"]));
        Assert.Equal(1, (int)match["winning_team"]![0]!);
        Assert.Equal(1, (int)match["players"]!["all"]![0]!["data"]!["EndOfMatchStats"]!["WinningTeamIndex"]!);
    }

    [Fact]
    public void A2v2ListsBothTeams()
    {
        var entry = Entry("m2", 100L, mode: "2v2", result: "loss", players:
        [
            Player(Me, "character_shaggy", 0, false), Player(Mate, "character_batman", 0, false),
            Player(Them, "character_jake", 1, true), Player(Other, "character_finn", 1, true),
        ]);
        var match = OnlyMatch(entry, "recent_matches_2v2");
        // The TS server listed the first other player (my teammate) as the winner.
        Assert.Equal([Them, Other], Strings(match["win"]));
        Assert.Equal([Me, Mate], Strings(match["loss"]));
        Assert.Equal(1, (int)match["winning_team"]![0]!);
        Assert.Equal("2v2", (string)match["server_data"]!["GameplayConfig"]!["ModeString"]!);
        Assert.Equal(4, (int)match["template"]!["max_players"]!);
    }

    [Fact]
    public void NamesFromThePlayerRecords()
    {
        var all = OnlyMatch(Entry("m1", 100L))["players"]!["all"]!.AsArray();
        Assert.Equal(["them", "me"], all.Select(p => (string)p!["identity"]!["username"]!));
        Assert.Equal(["them", "me"], all.Select(p => (string)p!["identity"]!["usernames"]![0]!["username"]!));
    }

    [Fact]
    public void FfaIsItsOwnTemplate()
    {
        var match = OnlyMatch(Entry("m3", 100L, mode: "FFA"), "recent_matches_2v2");
        Assert.Equal("ffa_container", (string)match["name"]!);
        Assert.Equal("ffa_container", (string)match["criteria"]!["slug"]!);
        Assert.Equal("ffa_container", (string)match["template"]!["slug"]!);
        Assert.Equal("FFA", (string)match["server_data"]!["GameplayConfig"]!["ModeString"]!);
    }

    [Fact]
    public void NoPlayerMarkedAsWinnerKeepsTheTsRule()
    {
        var match = OnlyMatch(Entry("m4", 100L, result: "win", players: [Player(Them, "unknown", 0, false), Player(Me, "character_shaggy", 1, false)]));
        Assert.Equal([Me], Strings(match["win"]));
        Assert.Equal([Them], Strings(match["loss"]));
        Assert.Equal(0, (int)match["winning_team"]![0]!);
    }
}

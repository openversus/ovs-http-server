using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Arenas;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Lobbies;

namespace OpenVersus.Server.Core.Tests.Arenas;

/// <summary>The Arena lobby the mode select's Arena button makes (create_arena_lobby). Needs no stores.</summary>
public sealed class ArenaLobbyTests
{
    private const string Me = "0000000000000000000a0001";

    private static JsonObject NewLobby(JsonObject? request = null) =>
        ArenaLobbyService.Lobby("6aca467b4a4abc2e3c7c6001", Me, 1_791_641_211, request, JsonValue.Create(448), "character_batman", "skin_batman_default", "195303.1.1");

    [Fact]
    public void TheGamesLobbyParserFindsWhatItRequires()
    {
        // UMvsPlayerLobby's parser (0x1428e9290): MatchID, LeaderID, ReadyPlayers and Teams, each required.
        var lobby = NewLobby();
        Assert.Equal("6aca467b4a4abc2e3c7c6001", (string)lobby["MatchID"]!);
        Assert.Equal(Me, (string)lobby["LeaderID"]!);
        Assert.Empty(lobby["ReadyPlayers"]!.AsObject());
        Assert.Equal("arena_lobby", (string)lobby["LobbyTemplate"]!);
    }

    [Fact]
    public void EightTeamsOfTwoWithTheCreatorAloneOnTheFirst()
    {
        // The lobby screen: two rows of four teams of two players.
        var teams = NewLobby()["Teams"]!.AsArray().Select(t => t!.AsObject()).ToList();
        Assert.Equal(Enumerable.Range(0, 8), teams.Select(t => (int)t["TeamIndex"]!));
        Assert.Equal([Me], teams[0]["Players"]!.AsObject().Select(p => p.Key));
        Assert.Equal(0, (int)teams[0]["Players"]![Me]!["LobbyPlayerIndex"]!);
        Assert.Equal(1, (int)teams[0]["Length"]!);
        Assert.All(teams.Skip(1), t => Assert.Empty(t["Players"]!.AsObject()));
    }

    [Fact]
    public void TheRequestsOwnValuesAreKept()
    {
        var request = new JsonObject
        {
            ["LobbyType"] = 0,
            ["AutoPartyPreference"] = true,
            ["HissCrc"] = 200016,
            ["Platform"] = "PC",
            ["AllMultiplayParams"] = new JsonObject { ["1"] = new JsonObject { ["MultiplayProfileId"] = "1252499" } },
        };
        var lobby = NewLobby(request);
        Assert.True((bool)lobby["PlayerAutoPartyPreferences"]![Me]!);
        Assert.Equal(200016, (int)lobby["HissCrc"]!);
        Assert.Equal("1252499", (string)lobby["AllMultiplayParams"]!["1"]!["MultiplayProfileId"]!);
        // GameplayPreferences is the value it is given, 0 included (see GameplayPreferences.Of).
        Assert.Equal(448, (int)lobby["PlayerGameplayPreferences"]![Me]!);
        Assert.Equal("character_batman", (string)lobby["LockedLoadouts"]![Me]!["Character"]!);
    }
    // An Arena lobby as stored: its players and their teams.
    private static ArenaLobby Stored(params (string Id, int Team)[] players) => (ArenaLobby)Lobby.FromJson("0000000000000000000a0100", Js.Stringify(new JsonObject
    {
        ["lobbyId"] = "0000000000000000000a0100", ["ownerId"] = players[0].Id, ["ownerUsername"] = "Leader", ["mode"] = ArenaLobby.TemplateName,
        ["playerIds"] = new JsonArray([.. players.Select(p => (JsonNode?)p.Id)]), ["createdAt"] = 0,
        ["teams"] = new JsonObject([.. players.Select(p => KeyValuePair.Create(p.Id, (JsonNode?)p.Team))]),
    }))!;

    [Fact]
    // An Arena always starts with all 16 places: the players on their teams, in lobby order, every empty place a bot's.
    public void TheRosterFillsEveryEmptyPlaceWithABot()
    {
        var lobby = Stored(("a", 0), ("b", 3), ("c", 0));
        Assert.Equal(13, lobby.EmptyPlaces);
        string[] bots = [.. Enumerable.Range(1, 13).Select(n => $"bot{n}")];

        var teams = lobby.Roster(bots);

        Assert.Equal(ArenaLobby.TeamCount, teams.Count);
        Assert.All(teams, t => Assert.Equal(ArenaLobby.TeamSize, t.Count));
        Assert.Equal(["a", "c"], teams[0]);
        Assert.Equal(["bot1", "bot2"], teams[1]);
        Assert.Equal(["b", "bot5"], teams[3]);
        Assert.Equal(ArenaLobby.Capacity, teams.SelectMany(t => t).Distinct().Count());
    }

    [Fact]
    public void AStartedArenaKeepsItsTeamsEachAt100Health()
    {
        var lobby = Stored(("a", 0));
        var teams = lobby.Roster([.. Enumerable.Range(1, 15).Select(n => $"bot{n}")]);
        Assert.False(lobby.IsStarted);

        lobby.Start(teams, 1_000);

        Assert.True(lobby.IsStarted);
        var read = (ArenaLobby)Lobby.FromJson(lobby.Id, lobby.ToJson())!;
        Assert.Equal(teams, read.ArenaTeams);
        var arena = JsonNode.Parse(lobby.ToJson())!["arena"]!;
        Assert.Equal(Enumerable.Repeat(100, 8), arena["life"]!.AsArray().Select(l => (int)l!));
        Assert.Equal(1, (int)arena["round"]!);
        Assert.Throws<ArgumentException>(() => lobby.Start([.. teams.Take(7)], 1_000));
    }

    [Fact]
    // Each round's 2v2s: every team in exactly one.
    public void APairingHasEveryTeamOnce()
    {
        for (int seed = 0; seed < 50; seed++)
        {
            var pairs = ArenaLobby.Pairings(new Random(seed));
            Assert.Equal(4, pairs.Count);
            Assert.Equal(Enumerable.Range(0, 8), pairs.SelectMany(p => new[] { p.A, p.B }).Order());
        }
    }
}

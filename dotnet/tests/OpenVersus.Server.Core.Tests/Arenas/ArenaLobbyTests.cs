using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Arenas;

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
}

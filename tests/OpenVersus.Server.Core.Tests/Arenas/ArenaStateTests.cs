using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Arenas;

namespace OpenVersus.Server.Core.Tests.Arenas;

/// <summary>The Arena in the game's shape (FMvsArenaData beside the gameplay config). Needs no stores.</summary>
public sealed class ArenaStateTests
{
    private static IReadOnlyList<IReadOnlyList<ArenaState.Participant>> Teams() =>
    [
        .. Enumerable.Range(0, 8).Select(t => (IReadOnlyList<ArenaState.Participant>)
        [
            .. Enumerable.Range(0, 2).Select(i => t == 0 && i == 0
                ? new ArenaState.Participant("me", "Me", false, "character_jason", "skin_jason_000", ["character_shaggy", "character_taz"])
                : new ArenaState.Participant($"bot{t}{i}", "Bot", true, "character_taz", "skin_taz_default", [])),
        ]),
    ];

    [Fact]
    // Every participant once in Players and PlayerInfo; eight teams team_0..team_7, each at the health given, with its
    // two participants; the round.
    public void TheArenaDataHoldsEveryParticipantAndTeam()
    {
        var data = ArenaState.Data("arena1", Teams(), [.. Enumerable.Repeat(100, 8)], 1);

        Assert.Equal("arena1", (string?)data["ArenaId"]);
        Assert.Equal(16, data["Players"]!.AsArray().Count);
        Assert.Equal(16, data["PlayerInfo"]!.AsObject().Count);
        Assert.Equal(Enumerable.Range(0, 8).Select(ArenaState.TeamId), data["TeamInfo"]!.AsObject().Select(e => e.Key));
        var first = data["TeamInfo"]!["team_0"]!;
        Assert.Equal((0, 100), ((int)first["TeamIndex"]!, (int)first["LifeRemaining"]!));
        Assert.Equal(["me", "bot01"], first["Players"]!.AsArray().Select(p => (string)p!));
        Assert.Equal(1, (int)data["CurrentRound"]!);
    }

    [Fact]
    // A player: their team, their fighter, the starting coins, the fighters they may pick; a bot is marked a bot.
    public void APlayerStartsWithCoinsAndAChoiceOfFighters()
    {
        var data = ArenaState.Data("arena1", Teams(), [.. Enumerable.Repeat(100, 8)], 1);
        var me = data["PlayerInfo"]!["me"]!;

        Assert.Equal(("me", "team_0", false), ((string?)me["AccountID"], (string?)me["TeamId"], (bool)me["bIsBot"]!));
        Assert.Equal("character_jason", (string?)me["Loadout"]!["Character"]);
        Assert.Equal(ArenaState.StartingCoins, (int)me["playerData"]!["CurrencyAmount"]!);
        Assert.Equal(["character_shaggy", "character_taz"], me["SelectableCharacters"]!.AsArray().Select(c => (string)c!));
        Assert.True((bool)data["PlayerInfo"]!["bot01"]!["bIsBot"]!);
    }

    [Fact]
    // Beside the config: the whole ArenaData, ArenaId beside it (the game reads it there unchecked: without it, it crashed),
    // the reader's own entry as ArenaPlayerInfo, and the constants.
    public void TheConfigDataCarriesTheArenaAndTheReader()
    {
        var data = ArenaState.Data("arena1", Teams(), [.. Enumerable.Repeat(100, 8)], 1);

        var beside = ArenaState.ConfigData(data, "me");

        Assert.Equal(data.ToJsonString(), beside["ArenaData"]!.ToJsonString());
        Assert.Equal("arena1", (string?)beside["ArenaId"]);
        Assert.Equal(data["PlayerInfo"]!["me"]!.ToJsonString(), beside["ArenaPlayerInfo"]!.ToJsonString());
        Assert.Equal(90, (int)beside["ArenaConstants"]!["RoundLength"]!);
    }

    [Fact]
    // The per-round tables the game's local shop indexes by round are as long as a run can last.
    public void ThePerRoundTablesCoverEveryRound()
    {
        var constants = ArenaState.Constants();

        foreach (string table in (string[])["HealthRoundValues", "HealthRoundScalars", "CurrencyPerRound", "CurrencyPerRingout", "ShopLevelWeights"])
        {
            Assert.Equal(ArenaState.MaxRounds, constants[table]!.AsArray().Count);
        }

        Assert.All(constants["ShopLevelWeights"]!.AsArray(), w => Assert.Equal(5, w!["RarityWeight"]!.AsObject().Count));
    }

    [Fact]
    public void TheModeInfoIsAnObject()
    {
        var info = ArenaState.ModeInfo(10, 30, readyToStart: false)["ArenaModeInfo"]!;

        Assert.Equal((10, 30, false), ((int)info["FaceoffWaitTime"]!, (int)info["ShopTime"]!, (bool)info["bReadyToStart"]!));
    }
}

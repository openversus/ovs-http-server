using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Lobbies;

namespace OpenVersus.Server.Core.Arenas;

// The Arena as the game reads it. A gameplay config with an ArenaModeInfo object is an Arena match (the parse callback
// 0x14297ae80 sets MatchType Arena on that field alone, after the PvP/Ranked/Custom flags), and the game's Arena
// subsystem reads two keys beside that config in OnGameplayConfigNotified's data (UMvsArenaSubsystem::
// HandleGameplayConfigParsed, 0x1426f5e40): ArenaData (FMvsArenaData) and ArenaPlayerInfo (the reader's FMvsArenaPlayer).
// JSON becomes the structs by reflection, so the keys are the UPROPERTY names (layouts: the UE4SS header dump). The same
// message is read by a second handler (0x1426f6960): it needs ArenaData and MatchId, then reads ArenaId BESIDE them
// (unchecked: a message without it crashed the game, bench 2026-10-11) and, when present, ArenaConstants
// (FMvsArenaConstantInfo: the round's numbers, the character select's time, the shop's tables, which the game's local
// shop indexes by round).
//
//   ArenaData: {ArenaId (the Arena lobby's id), Players (every participant's id), PlayerInfo {id: player},
//               TeamInfo {TeamId: team}, CurrentRound}
//   player:    {Username, AccountID, TeamId, bIsBot, AccountInfo {Name, Banner, RingOutVfx, ProfileIcon},
//               Loadout {Character, Skin}, playerData {Inventory, CurrencyAmount, InterestPer, ShopRerollCost,
//               FreeShopRerolls}, Stats, CurrentShop, CurrentShopLocal, SelectableCharacters}
//   team:      {TeamId, TeamIndex, LifeRemaining, FinalRank, Stats {Wins, Losses, Draws, WinStreak, LoseStreak,
//               MatchStats}, Players, RoundOpponents, Rank}
//
// Numbers WB's server held, which the client never had (docs/ARENAS.md), RECONSTRUCTED: 10 starting coins (WB's Developer
// Series Q&A), interest 1 per 6 coins (patch 1.4.1). The shop reroll cost is not known: a placeholder. TeamId's spelling
// (team_N) follows the game's own Arena analytics.

/// <summary>The Arena's state in the game's shape.</summary>
public static class ArenaState
{
    /// <summary>Coins each player starts with (reconstructed: WB's "We start every player with 10 coins").</summary>
    public const int StartingCoins = 10;

    /// <summary>Coins per point of interest (reconstructed: patch 1.4.1, "1 per 6 to start").</summary>
    public const int InterestPer = 6;

    /// <summary>A shop reroll's cost: NOT KNOWN, a placeholder until a source says.</summary>
    public const int ShopRerollCost = 2;

    /// <summary>Fighters a player is offered at the start (reconstructed: patch notes, "from 4 to 6"; the wiki says 8).</summary>
    public const int SelectableCount = 6;

    /// <summary>ArenaModeInfo's waits, in seconds: NOT KNOWN, placeholders for the first probes.</summary>
    public const int FaceoffWaitSeconds = 10, ShopSeconds = 30;

    /// <summary>A round's length, in seconds (WB: "Games Are Played Out Over 90 Seconds").</summary>
    public const int RoundSeconds = 90;

    /// <summary>A participant: a player or a bot, with the fighter they show and, for a player, the fighters they may pick.</summary>
    public sealed record Participant(string Id, string Username, bool IsBot, string Character, string Skin, IReadOnlyList<string> Selectable);

    /// <summary>The TeamId of the team at <paramref name="teamIndex"/>.</summary>
    public static string TeamId(int teamIndex) => $"team_{teamIndex}";

    /// <summary>
    /// ArenaData for Arena <paramref name="arenaId"/>: <paramref name="teams"/> by team index, each team's
    /// <paramref name="life"/>, round <paramref name="round"/>.
    /// </summary>
    public static JsonObject Data(string arenaId, IReadOnlyList<IReadOnlyList<Participant>> teams, IReadOnlyList<int> life, int round)
    {
        var players = new JsonArray();
        var playerInfo = new JsonObject();
        var teamInfo = new JsonObject();
        for (int t = 0; t < teams.Count; t++)
        {
            foreach (var participant in teams[t])
            {
                players.Add(participant.Id);
                playerInfo[participant.Id] = Player(participant, t);
            }

            teamInfo[TeamId(t)] = new JsonObject
            {
                ["TeamId"] = TeamId(t),
                ["TeamIndex"] = t,
                ["LifeRemaining"] = life[t],
                ["FinalRank"] = 0,
                ["Stats"] = new JsonObject
                {
                    ["Wins"] = 0, ["Losses"] = 0, ["Draws"] = 0, ["WinStreak"] = 0, ["LoseStreak"] = 0, ["MatchStats"] = MatchStats(),
                },
                ["Players"] = new JsonArray([.. teams[t].Select(p => (JsonNode?)p.Id)]),
                ["RoundOpponents"] = new JsonArray(),
                ["Rank"] = 0,
            };
        }

        return new JsonObject
        {
            ["ArenaId"] = arenaId,
            ["Players"] = players,
            ["PlayerInfo"] = playerInfo,
            ["TeamInfo"] = teamInfo,
            ["CurrentRound"] = round,
        };
    }

    /// <summary>One participant as FMvsArenaPlayer, on team <paramref name="teamIndex"/>, at the Arena's start.</summary>
    public static JsonObject Player(Participant participant, int teamIndex) => new()
    {
        ["Username"] = participant.Username,
        ["AccountID"] = participant.Id,
        ["TeamId"] = TeamId(teamIndex),
        ["bIsBot"] = participant.IsBot,
        ["AccountInfo"] = new JsonObject { ["Name"] = participant.Username, ["Banner"] = "", ["RingOutVfx"] = "", ["ProfileIcon"] = "" },
        ["Loadout"] = new JsonObject { ["Character"] = participant.Character, ["Skin"] = participant.Skin },
        ["playerData"] = new JsonObject
        {
            ["Inventory"] = new JsonArray(),
            ["CurrencyAmount"] = StartingCoins,
            ["InterestPer"] = InterestPer,
            ["ShopRerollCost"] = ShopRerollCost,
            ["FreeShopRerolls"] = 0,
        },
        ["Stats"] = new JsonObject
        {
            ["MatchStats"] = MatchStats(),
            ["InterestGained"] = 0, ["CurrencySpent"] = 0, ["ItemsLeveled"] = 0, ["ItemsPurchased"] = 0, ["ItemsSold"] = 0, ["ShopRerolls"] = 0,
            ["bRandomCharacter"] = false,
        },
        ["CurrentShop"] = new JsonArray(),
        ["CurrentShopLocal"] = new JsonArray(),
        ["SelectableCharacters"] = new JsonArray([.. participant.Selectable.Select(c => (JsonNode?)c)]),
    };

    private static JsonObject MatchStats() => new()
    {
        ["RINGOUTS"] = 0, ["Damage"] = 0, ["DamagedAdded"] = 0, ["DamageMitigated"] = 0, ["KnockbackAdded"] = 0, ["KnockbackMitigated"] = 0,
        ["HealingReceived"] = 0, ["GreyHealthReceived"] = 0,
    };

    /// <summary>
    /// What a round's Arena match carries beside its gameplay config (OnGameplayConfigNotified's data): ArenaData,
    /// ArenaId (required beside it), ArenaPlayerInfo for <paramref name="reader"/> (one message goes to every player of the
    /// match), ArenaConstants.
    /// </summary>
    public static JsonObject ConfigData(JsonObject arenaData, string reader) => new()
    {
        ["ArenaData"] = arenaData.DeepClone(),
        ["ArenaId"] = arenaData["ArenaId"]?.DeepClone(),
        ["ArenaPlayerInfo"] = arenaData["PlayerInfo"]?[reader]?.DeepClone(),
        ["ArenaConstants"] = Constants(),
    };

    /// <summary>The rounds a run can last (the wiki: "winners play up to 13 rounds"): the per-round tables' length.</summary>
    public const int MaxRounds = 13;

    /// <summary>
    /// FMvsArenaConstantInfo. R = reconstructed from a source (docs/ARENAS.md: WB's rules text, the wiki, patch 1.4.1,
    /// WB's Developer Series Q&A); P = PLACEHOLDER, no source, to become settings once the flow works.
    /// </summary>
    public static JsonObject Constants()
    {
        static JsonArray Rounds(Func<int, JsonNode> value) => new([.. Enumerable.Range(0, MaxRounds).Select(value)]);
        return new JsonObject
        {
            ["RoundLength"] = RoundSeconds,                                          // R: "Played Out Over 90 Seconds"
            ["HealthRoundValues"] = Rounds(r => 15 + 5 * r),                         // P: rising ("the later the rounds, the more damage")
            ["HealthRoundScalars"] = Rounds(_ => 1.0),                               // P
            ["CurrencyPerRound"] = Rounds(_ => 5),                                   // P
            ["CurrencyForWin"] = 3,                                                  // P
            ["CurrencyForWinStreak"] = new JsonArray(0, 1, 2, 3),                    // P
            ["CurrencyForLoseStreak"] = new JsonArray(0, 1, 2, 3),                   // P
            ["CurrencyPerRingout"] = Rounds(_ => 1),                                 // P
            ["CharacterSelectTime"] = 30,                                            // P
            ["ShopCharacterRerollCost"] = 3,                                         // R: wiki, "reroll 3 coins"
            ["ShopRerollCost"] = ShopRerollCost,                                     // P
            ["CurrencyForRandomCharacterSelect"] = 2,                                // R: wiki "2 coins" + the game's text (a random fighter GAINS coins)
            ["InterestPer"] = InterestPer,                                           // R: patch 1.4.1 "1 per 6"
            ["MaxInterest"] = 10,                                                    // R: patch 1.4.1 "more at max (10)"
            ["FaceoffWaitTime"] = FaceoffWaitSeconds,                                // P
            ["ItemXpPerLevel"] = new JsonArray(1, 3),                                // R: patch 1.4.1 "1 for level 2, 3 for level 3"
            ["MaxItemsInInventory"] = 6,                                             // P
            // P: weights by rarity per round, rarer later ("Rarer and more powerful items will show up in later rounds").
            ["ShopLevelWeights"] = Rounds(r => new JsonObject
            {
                ["RarityWeight"] = new JsonObject
                {
                    ["Common"] = Math.Max(0, 50 - 5 * r), ["Uncommon"] = 30, ["Rare"] = 10 + 3 * r, ["Epic"] = 2 * r, ["Legendary"] = Math.Max(0, r - 4),
                },
            }),
            // P: the shared pool's copies per rarity ("the shop's inventory is shared between all players").
            ["ItemAmountsByRarity"] = new JsonObject { ["Common"] = 10, ["Uncommon"] = 8, ["Rare"] = 6, ["Epic"] = 4, ["Legendary"] = 2 },
            ["InterestPerBoost"] = new JsonArray(),                                  // P
        };
    }

    /// <summary>The gameplay config fields that make a match an Arena round: ArenaModeInfo (its presence decides).</summary>
    public static JsonObject ModeInfo(int faceoffWaitSeconds, int shopSeconds, bool readyToStart) => new()
    {
        ["ArenaModeInfo"] = new JsonObject { ["FaceoffWaitTime"] = faceoffWaitSeconds, ["ShopTime"] = shopSeconds, ["bReadyToStart"] = readyToStart },
    };

    /// <summary>The Arena's teams as stored on its lobby (<see cref="ArenaLobby.ArenaTeams"/>), as participants.</summary>
    public static IReadOnlyList<IReadOnlyList<Participant>> Teams(ArenaLobby arena, Func<string, Participant> participant) =>
        [.. arena.ArenaTeams.Select(t => (IReadOnlyList<Participant>)[.. t.Select(participant)])];
}

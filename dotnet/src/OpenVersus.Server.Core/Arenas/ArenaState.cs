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

    /// <summary>
    /// ArenaData for <paramref name="run"/> as it stands: each player's offer (in the character select, until they pick),
    /// fighter, coins, items and this round's shop pages (in the shop); each team's health, record, streaks, opponents
    /// (RoundOpponents, read by the game's Arena Status before the match; a mirror opponent is written as the team's own
    /// TeamId, P) and rank.
    /// </summary>
    public static JsonObject Data(ArenaRun run, ArenaRules rules)
    {
        var players = new JsonArray();
        var playerInfo = new JsonObject();
        var teamInfo = new JsonObject();
        var standing = run.Teams.Where(t => t.In).OrderByDescending(t => t.Life).Select(t => t.Index).ToList();
        foreach (var team in run.Teams)
        {
            foreach (string id in team.Members)
            {
                players.Add(id);
                playerInfo[id] = Player(run, run.Players[id], rules);
            }

            teamInfo[TeamId(team.Index)] = new JsonObject
            {
                ["TeamId"] = TeamId(team.Index),
                ["TeamIndex"] = team.Index,
                ["LifeRemaining"] = Math.Max(0, team.Life),
                ["FinalRank"] = team.FinalRank,
                ["Stats"] = new JsonObject
                {
                    ["Wins"] = team.Wins, ["Losses"] = team.Losses, ["Draws"] = 0, ["WinStreak"] = team.WinStreak, ["LoseStreak"] = team.LoseStreak,
                    ["MatchStats"] = MatchStats(),
                },
                ["Players"] = new JsonArray([.. team.Members.Select(m => (JsonNode?)m)]),
                ["RoundOpponents"] = new JsonArray([.. team.Opponents.Select(o => (JsonNode?)TeamId(o == ArenaRun.Mirror ? team.Index : o))]),
                ["Rank"] = team.In ? standing.IndexOf(team.Index) + 1 : team.FinalRank,
            };
        }

        return new JsonObject
        {
            ["ArenaId"] = run.Id,
            ["Players"] = players,
            ["PlayerInfo"] = playerInfo,
            ["TeamInfo"] = teamInfo,
            ["CurrentRound"] = run.Round,
        };
    }

    /// <summary>What a reader's config carries beside it for <paramref name="run"/>: <see cref="ConfigData(JsonObject, string)"/> of its ArenaData.</summary>
    public static JsonObject ConfigData(ArenaRun run, string reader, ArenaRules rules)
    {
        var data = ConfigData(Data(run, rules), reader);
        data["ArenaConstants"] = rules.Constants();
        return data;
    }

    private static JsonObject Player(ArenaRun run, ArenaRunPlayer player, ArenaRules rules)
    {
        bool selecting = run.Phase == ArenaPhase.CharacterSelect && player.Character is null;
        bool shopping = run.Phase == ArenaPhase.Shop && !player.IsBot;
        var pages = shopping ? player.ShopPages : [];
        return new JsonObject
        {
            ["Username"] = player.Username,
            ["AccountID"] = player.Id,
            ["TeamId"] = TeamId(player.Team),
            ["bIsBot"] = player.IsBot,
            ["AccountInfo"] = new JsonObject { ["Name"] = player.Username, ["Banner"] = "", ["RingOutVfx"] = "", ["ProfileIcon"] = "" },
            ["Loadout"] = new JsonObject { ["Character"] = player.Character ?? "", ["Skin"] = player.Skin ?? "" },
            ["playerData"] = PlayerData(player, rules),
            ["Stats"] = new JsonObject
            {
                ["MatchStats"] = MatchStats(),
                ["InterestGained"] = 0, ["CurrencySpent"] = 0, ["ItemsLeveled"] = 0, ["ItemsPurchased"] = 0, ["ItemsSold"] = 0, ["ShopRerolls"] = 0,
                ["bRandomCharacter"] = player.RandomPick,
            },
            ["CurrentShop"] = new JsonArray([.. (pages.Count > 0 ? pages[0] : []).Select(slug => (JsonNode?)Offer(slug, rules))]),
            // The game reads the first page unchecked when its shop opens (crash 2026-10-11 when it was empty).
            ["CurrentShopLocal"] = new JsonArray([.. pages.Select(page => (JsonNode?)new JsonObject
            {
                ["CurrentShop"] = new JsonArray([.. page.Select(slug => (JsonNode?)Offer(slug, rules))]),
            })]),
            // The game opens the character select while its reader has fighters on offer, else the shop.
            ["SelectableCharacters"] = new JsonArray([.. (selecting ? player.Pool : []).Select(c => (JsonNode?)c)]),
        };
    }

    /// <summary>FMvsArenaPlayerData: what the player holds (their items, coins) and what the shop charges them.</summary>
    public static JsonObject PlayerData(ArenaRunPlayer player, ArenaRules rules) => new()
    {
        ["Inventory"] = new JsonArray([.. player.Inventory.Select(i => (JsonNode?)Owned(i, rules))]),
        ["CurrencyAmount"] = player.Coins,
        ["InterestPer"] = rules.InterestPer,
        ["ShopRerollCost"] = rules.ShopRerollCost,
        ["FreeShopRerolls"] = 0,
    };

    /// <summary>The answer to an Arena call (FMvsArenaBaseResponse's two fields the game was seen to take, 2026-10-11).</summary>
    public static JsonObject Answer(bool success, string playerId) => new() { ["bSuccess"] = success, ["playerId"] = playerId };

    /// <summary>
    /// The answer to arena_reroll_characters (FMvsArenaCharacterRerollResponse): the offer and the coins after it. The game
    /// takes both from here (seen 2026-10-11: an answer without them emptied its offer and showed 0 coins).
    /// </summary>
    public static JsonObject RerollAnswer(bool success, ArenaRunPlayer player, ArenaRules rules) => new()
    {
        ["BaseResponse"] = new JsonObject { ["bSuccess"] = success, ["playerId"] = player.Id, ["FailureReason"] = success ? "" : "refused" },
        ["SelectableCharacters"] = new JsonArray([.. player.Pool.Select(c => (JsonNode?)c)]),
        ["playerData"] = PlayerData(player, rules),
    };

    private static JsonObject Offer(string slug, ArenaRules rules)
    {
        int cost = ArenaItems.Of(slug)?.Cost ?? 0;
        return new JsonObject
        {
            ["Item"] = new JsonObject
            {
                ["Slug"] = slug, ["Level"] = 1, ["XP"] = 0, ["NextLevelXp"] = rules.ItemXpPerLevel.Count > 0 ? rules.ItemXpPerLevel[0] : 0, ["SellValue"] = cost / 2,
            },
            ["Cost"] = cost,
        };
    }

    private static JsonObject Owned(ArenaOwnedItem owned, ArenaRules rules) => new()
    {
        ["Slug"] = owned.Slug,
        ["Level"] = owned.Level,
        ["XP"] = owned.Xp,
        ["NextLevelXp"] = owned.Level - 1 < rules.ItemXpPerLevel.Count ? rules.ItemXpPerLevel[owned.Level - 1] : 0,
        ["SellValue"] = ArenaRun.SellValue(owned),
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

    /// <summary>The rounds a run can last: the per-round tables' length (<see cref="ArenaRules.MaxRounds"/>).</summary>
    public static int MaxRounds => ArenaRules.Default.MaxRounds;

    /// <summary>FMvsArenaConstantInfo for the default rules (<see cref="ArenaRules.Constants"/>).</summary>
    public static JsonObject Constants() => ArenaRules.Default.Constants();

    /// <summary>The gameplay config fields that make a match an Arena round: ArenaModeInfo (its presence decides).</summary>
    public static JsonObject ModeInfo(int faceoffWaitSeconds, int shopSeconds, bool readyToStart) => new()
    {
        ["ArenaModeInfo"] = new JsonObject { ["FaceoffWaitTime"] = faceoffWaitSeconds, ["ShopTime"] = shopSeconds, ["bReadyToStart"] = readyToStart },
    };

    /// <summary>The Arena's teams as stored on its lobby (<see cref="ArenaLobby.ArenaTeams"/>), as participants.</summary>
    public static IReadOnlyList<IReadOnlyList<Participant>> Teams(ArenaLobby arena, Func<string, Participant> participant) =>
        [.. arena.ArenaTeams.Select(t => (IReadOnlyList<Participant>)[.. t.Select(participant)])];
}

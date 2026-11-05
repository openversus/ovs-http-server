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
        // The round's shop pages from the shop's opening to the next round's: the game's matchup screen, re-activated (back
        // from Arena Status, say), starts its pre-match states again, opens the shop for a player with no fighters on offer,
        // and reads its first page unchecked (crash 2026-10-11 with none, in the Match phase).
        var pages = !player.IsBot && !selecting ? player.ShopPages : [];
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
    /// The player's items as the match applies them: their gameplay config entry's Gems, one {Gem, ChargeLevel} per
    /// owned slot (the item's gem slug; ChargeLevel = its level - 1). The game's player parser reads exactly these two
    /// keys (0x14297caa0) and resolves Gem as a MvsGemHsda slug. At spawn each gem buff gets the Payload of its tier
    /// ChargeLevelData[min(ChargeLevel, tiers - 1)] (0x1424d3bd0, 0-based: level 1 is the first tier), which the item's
    /// buff reads as its number (the chicken's seconds). Gems apply only under GemAttunement. Archive arenas-protocol.md,
    /// "2026-10-11 (s48)".
    /// </summary>
    public static JsonArray Gems(ArenaRunPlayer player) => new([.. player.Inventory
        .Where(slot => !slot.IsEmpty && ArenaItems.Of(slot.Slug) is not null)
        .Select(slot => (JsonNode?)new JsonObject { ["Gem"] = ArenaItems.Of(slot.Slug)!.Gem, ["ChargeLevel"] = Math.Max(0, slot.Level - 1) })]);

    /// <summary>
    /// The config's TeamData for an Arena round: no score target for either side, so only the timer ends the match and
    /// the most stocks taken wins. The game's match setup (0x1429886e0) takes TeamData's targets per team and never ends a
    /// match on a target of 0 or less (OnScoreChanged 0x142968280); with TeamData empty it falls back to NumRingouts, and
    /// to 4 for a 2v2 when that is 0 (bench 2026-10-11: rounds ended at 4 stocks).
    /// </summary>
    public static JsonArray NoScoreTarget() => [new JsonObject { ["TargetScore"] = 0 }, new JsonObject { ["TargetScore"] = 0 }];

    /// <summary>
    /// The config's RiftNodeAttunement for an Arena round. The game applies a player's gems at spawn (0x142989190) only
    /// when this tag is one of the gem's Attunements or is Attunements.Any (0x142755170); Arena gems carry no Attunements.
    /// The base config's "Attunements:None" (and the parser's default when the key is absent) skipped every item (bench
    /// 2026-10-11, three Arenas). The parser turns ':' into '.' before looking the tag up (0x140cd8c30).
    /// </summary>
    public const string GemAttunement = "Attunements:Any";

    /// <summary>
    /// OnArenaNextMatch's data: the round's match a player's game goes to from its post-match screen. Its reader
    /// (0x142678860, reached from the Arena post-match state's binding of the template) takes MatchId and Cluster; the
    /// PriorMatchId test before it is skipped for an Arena (archive arenas-protocol.md, "2026-10-11 (s47)").
    /// </summary>
    public static JsonObject NextMatch(string matchId) => new()
    {
        ["template_id"] = "OnArenaNextMatch",
        ["MatchId"] = matchId,
        ["Cluster"] = Matches.LobbyDocuments.Cluster,
    };

    /// <summary>
    /// ArenaPlayerEndPayload's data: the end of a reader's Arena (their team out, or the winner). The game's handler
    /// (0x1426f62f0, reached from the template and from an arena_match_absent answer with return_code 13 through
    /// 0x1426f7d00's template_id test) needs ArenaData and ArenaId beside it, refreshes the Arena from them, marks the
    /// player's Arena ended (the subsystem's +0xe0, cleared by the next Arena config), and the post-match screen leaves on
    /// that mark. No ClientReturnData: the game then shows no Arena points or rewards. Archive arenas-protocol.md, "2026-10-11 (s48)".
    /// </summary>
    public static JsonObject PlayerEnd(ArenaRun run, string reader, ArenaRules rules)
    {
        var data = ConfigData(run, reader, rules);
        data["template_id"] = "ArenaPlayerEndPayload";
        return data;
    }

    /// <summary>
    /// The answer to arena_reroll_characters (FMvsArenaCharacterRerollResponse): the offer and the coins after it. The game
    /// takes both from here (seen 2026-10-11: an answer without them emptied its offer and showed 0 coins).
    /// </summary>
    /// <remarks>Under "Result": the game's handler (0x142a536f0) reads the answer's body.Result (archive arenas-protocol.md).</remarks>
    public static JsonObject RerollAnswer(bool success, ArenaRunPlayer player, ArenaRules rules) => new()
    {
        ["Result"] = new JsonObject
        {
            ["BaseResponse"] = new JsonObject { ["bSuccess"] = success, ["playerId"] = player.Id, ["FailureReason"] = success ? "" : "refused" },
            ["SelectableCharacters"] = new JsonArray([.. player.Pool.Select(c => (JsonNode?)c)]),
            ["playerData"] = PlayerData(player, rules),
        },
    };

    private static JsonObject Offer(string slug, ArenaRules rules)
    {
        int cost = ArenaItems.Of(slug)?.Cost ?? 0;
        return new JsonObject
        {
            ["Item"] = new JsonObject
            {
                ["Slug"] = slug, ["Level"] = 1, ["XP"] = 0, ["NextLevelXp"] = NextLevelXp(1, rules), ["SellValue"] = cost / 2,
            },
            ["Cost"] = cost,
        };
    }

    // A slot (FMvsArenaItemData); an empty one has Slug "" (the game's local shop fills the first of those).
    private static JsonObject Owned(ArenaOwnedItem owned, ArenaRules rules) => new()
    {
        ["Slug"] = owned.Slug,
        ["Level"] = owned.Level,
        ["XP"] = owned.Xp,
        ["NextLevelXp"] = owned.IsEmpty ? 0 : NextLevelXp(owned.Level, rules),
        ["SellValue"] = owned.SellValue,
    };

    private static int NextLevelXp(int level, ArenaRules rules) => level < rules.ItemXpPerLevel.Count ? rules.ItemXpPerLevel[level] : 0;

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

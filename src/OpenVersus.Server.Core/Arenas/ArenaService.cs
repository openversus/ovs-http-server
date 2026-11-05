using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenVersus.Server.Core.Clients;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Hiss;
using OpenVersus.Server.Core.Lobbies;
using OpenVersus.Server.Core.Matches;
using OpenVersus.Server.Core.Matchmaking;
using OpenVersus.Server.Core.Realtime;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Arenas;

// The Arena, for the arenas service (its own executable: the Arena lobby and its matchmaking ticket stay the lobbies
// service's): the run (ArenaRun, kept by ArenaRunStore) and what its transitions ask for (ArenaEffect), carried out here.
//
// start_arena_direct (ArenaMatchmaking.cs): the Arena starts, always with all 16 places (8 teams of 2): the lobby's
// players on their teams, every empty place a Medium bot's (bot_config:{bot}, its fighter kept in the run too), marked
// started on the Arena lobby (once: a second start finds it started), and the run begins (round 1 paired, its matches
// launched, the character select open). Answered with the TS catch-all, which the game takes as "match found".
//
// The effects, after the run is written:
//   LaunchPairing     the pairing's 2v2 on a node, under its MatchId, named an Arena's (arena_match:{match} -> the run,
//                     ArenaResults: its end comes back to the run, never as a Casual rematch); from round 2 on, each of its
//                     players' games is first sent OnArenaNextMatch {MatchId, Cluster} (its post-match screen listens for it
//                     after its check-in and leaves for the next match on it; archive "2026-10-11 (s47)"). Then the match:
//                     team A on side 0 (even player indexes), B on side 1;
//                     a mirror (B = Mirror) is fresh bots copying A's fighters. Each player goes in with their pick (before
//                     the first pick: their lobby loadout), recorded as their played loadout, which the config reads. An
//                     Arena round: ArenaModeInfo in the config (its presence makes the match an Arena), timed
//                     (RoundSeconds), no ring-out target; each player's own Arena data beside it (ArenaState.ConfigData).
//   SendPhaseConfigs  every player still in is sent their kept config (match_config:{player}) with their Arena data and
//                     the match's fighters as the run has them now: the character select's end opens the shop this way,
//                     and the shops' end (the Match phase) sends it with ArenaModeInfo.bReadyToStart true, the only thing
//                     that takes the game from its shop to the match (ShopBaseState 0x1426a1dc0, archive).
//                     A player whose match was launched by the same step is skipped (the launch carries their data); a
//                     kept config of another match (the last round's) is never patched: an error, logged.
//   TeamsEnded        each player of a team whose Arena has ended (out this round, or the winner at the end) is sent
//                     ArenaPlayerEndPayload (ArenaState.PlayerEnd: their Arena data); their post-match screen leaves on it.
//   ArenaFinished     logged.
//
// The Arena's own calls (AnswerAsync), each a transition of the run named by ArenaLobbyId (ArenaParentId for the round's
// check-ins), applied under compare-and-set, its effects carried out, and answered {bSuccess, playerId} (bSuccess: the
// run took it) with the envelope's return_code 0 unless said:
//   arena_reroll_characters        RerollCharacters; answered with the new offer and the coins (RerollAnswer)
//   arena_select_character         Select {CharacterSlug, SkinSlug} (Random's +2 is not paid: what the Random button sends
//                                  with a full offer is unprobed; only "" with an empty one was seen)
//   arena_select_character_absent  SelectAbsent (a random fighter from the offer, no bonus)
//   arena_player_shop_closed       ShopClosed {ArenaRound, ShopPhaseDetails {NumRerolls, ItemTransactions [{bPurchase,
//                                  LocalShopIndex, ItemIndex}]}}
//   arena_shop_absent              ShopAbsent {ArenaRound}
//   arena_checkin                  Checkin {ArenaParentId, ArenaRound}: the game left its match (it sends this itself when
//                                  its post-match countdown runs out); answered success (the game then waits)
//   arena_match_absent             Checkin too (the game is still waiting); return_code 2 while the run waits for the next
//                                  round, which the game takes as "keep waiting" (it reports absent again later); from a
//                                  player whose Arena has ended, return_code 13 with the ArenaPlayerEndPayload body
//   the others                     answered success, not acted on: arena_purchase_item, arena_sell_item, arena_reroll_shop
//                                  (the shop runs in the game while ArenaShopLocalOperations is on).
// A node match's end (MatchEndedAsync, from the arena:results stream, ArenaResults): the run's MatchResult with the side
// the reports name and the run's players still connected (online_players), its effects carried out.
// The deadlines (DeadlinesAsync, the arenas service's loop): each run whose phase's deadline has passed is ticked (absent
// picks, shops closed with nothing bought, node matches that never ended decided as with no report, the next round started
// without the games that never checked in) and its effects carried out; a run gone (expired) loses its deadline entry.
//
// KNOWN GAPS: the effects run after the run's write, so a crash between the two loses that step's launches and configs; a
// launch that fails (no rollback port) leaves its pairing to the Match deadline; what becomes of the Arena lobby after a
// player's Arena has ended is not handled here; the taunts in the config are the lobby fighter's, not the pick's.

public interface IArenaService
{
    /// <summary>start_arena_direct: the game stopped waiting in Arena matchmaking; its Arena lobby starts with bots.</summary>
    Task<JsonObject> StartAsync(PartyRequest request, CancellationToken ct);

    /// <summary>One of the Arena's own calls (<paramref name="call"/>, e.g. arena_select_character): its answer.</summary>
    Task<ArenaAnswer> AnswerAsync(string call, PartyRequest request, CancellationToken ct);

    /// <summary>An Arena node match's end (ArenaResults): <paramref name="winnerSide"/> the side its reports name, null for none.</summary>
    Task MatchEndedAsync(string runId, string matchId, int? winnerSide, CancellationToken ct);

    /// <summary>Acts on every run whose deadline has passed (see the header).</summary>
    Task DeadlinesAsync(CancellationToken ct);
}

/// <summary>An Arena call's answer: the body, and the SSC envelope's return_code (the game's post-match screen reads it).</summary>
public sealed record ArenaAnswer(JsonObject Body, int ReturnCode = 0)
{
    /// <summary>arena_match_absent's "keep waiting": the game reports absent again later (0x14285bcd0, archive s47).</summary>
    public const int KeepWaiting = 2;

    /// <summary>arena_match_absent's "your Arena has ended": the game hands the body to its ArenaPlayerEndPayload handler (archive s48).</summary>
    public const int PlayerEnded = 13;
}

internal sealed class ArenaService(IServiceProvider services, IClientUpdateGate gate, IMatchLauncher launcher, IArenaBotMatches botMatches,
    TimeProvider time, ILogger<ArenaService> log) : IArenaService
{
    private static ArenaRules Rules => ArenaRules.Default;

    private IDatabase Redis() => services.GetService<IConnectionMultiplexer>()?.GetDatabase() ?? throw new InvalidOperationException("this service has no Redis (REDIS)");

    public async Task<JsonObject> StartAsync(PartyRequest request, CancellationToken ct)
    {
        var redis = Redis();
        string me = request.AccountId;
        string? lobbyId = request.Body?["ArenaLobbyId"] is JsonValue sent && sent.TryGetValue(out string? named) && named.Length > 0 ? named : await LobbyStore.PointerAsync(redis, me);
        if (lobbyId is null || await LobbyStore.GetAsync(redis, lobbyId) is not ArenaLobby arena || !arena.PlayerIds.Contains(me))
        {
            log.LogWarning("start_arena_direct from {Player}: not in Arena lobby {Lobby}; nothing started", me, lobbyId);
            return await TsCatchAll.AnswerAsync(services, ct);
        }

        if (arena.IsStarted)
        {
            log.LogInformation("start_arena_direct from {Player}: Arena lobby {Lobby} has started already", me, lobbyId);
            return await TsCatchAll.AnswerAsync(services, ct);
        }

        if (await gate.BlockOutdatedAsync(arena.PlayerIds, log, "an Arena"))
        {
            return gate.FailureBody();
        }

        var bots = new List<string>();
        var botFighters = new Dictionary<string, (string Character, string Skin)>();
        foreach (var fighter in BotFighters.Pick(arena.EmptyPlaces, Random.Shared))
        {
            string bot = await MatchmakingRequestService.BotAsync(redis, fighter);
            bots.Add(bot);
            botFighters[bot] = fighter;
        }

        var teams = arena.Roster(bots);
        bool started = false;
        await LobbyStore.UpdateAsync(redis, lobbyId, lobby =>
        {
            if (lobby is not ArenaLobby current || current.IsStarted)
            {
                return LobbyWrite.Keep;
            }

            current.Start(teams, time.GetUtcNow().ToUnixTimeMilliseconds());
            started = true;
            return LobbyWrite.Save;
        });
        if (!started)
        {
            log.LogInformation("start_arena_direct from {Player}: Arena lobby {Lobby} was started meanwhile", me, lobbyId);
            return await TsCatchAll.AnswerAsync(services, ct);
        }

        foreach (string pid in arena.PlayerIds)
        {
            await MatchmakingRequestService.RemoveTicketsAsync(redis, pid, [ArenaMatchmaking.List], log);
        }

        log.LogInformation("Arena lobby {Lobby} started by {Player}: {Players} players, {Bots} bots; teams {Teams}", lobbyId, me, arena.PlayerIds.Count, bots.Count,
            string.Join(" | ", teams.Select(t => string.Join(",", t))));

        // The run: every place by team, the players named as their sessions name them; the bots' fighters as picked above.
        var places = new List<IReadOnlyList<(string Id, string Username, bool IsBot)>>();
        foreach (var team in teams)
        {
            var members = new List<(string, string, bool)>();
            foreach (string pid in team)
            {
                if (botFighters.ContainsKey(pid))
                {
                    members.Add((pid, "Bot", true));
                    continue;
                }

                var connection = await HashAsync(redis, $"connections:{pid}");
                members.Add((pid, Or(Get(connection, "username"), Get(connection, "hydraUsername"), "Unknown"), false));
            }

            places.Add(members);
        }

        var (run, step) = ArenaRun.Start(lobbyId, places, BotFighters.All, Rules, Random.Shared, time.GetUtcNow().ToUnixTimeMilliseconds());
        foreach (var (bot, fighter) in botFighters)
        {
            (run.Players[bot].Character, run.Players[bot].Skin) = fighter;
        }

        if (!await ArenaRunStore.CreateAsync(redis, run))
        {
            log.LogWarning("Arena {Run}: a run was there already; nothing started", lobbyId);
            return await TsCatchAll.AnswerAsync(services, ct);
        }

        await CarryOutAsync(redis, run, step.Effects, ct);
        return await TsCatchAll.AnswerAsync(services, ct);
    }

    public async Task<ArenaAnswer> AnswerAsync(string call, PartyRequest request, CancellationToken ct)
    {
        var redis = Redis();
        string me = request.AccountId;
        var body = request.Body ?? [];
        int round = body["ArenaRound"] is JsonValue r && r.TryGetValue(out int n) ? n : 0;
        long now = time.GetUtcNow().ToUnixTimeMilliseconds();
        var random = Random.Shared;
        Func<ArenaRun, ArenaStep>? transition = call switch
        {
            "arena_reroll_characters" => run => run.RerollCharacters(me, BotFighters.All, Rules, random),
            "arena_select_character" => run => run.Select(me, Text(body["CharacterSlug"]), Text(body["SkinSlug"]), chooseRandom: false, BotFighters.All, Rules, random, now),
            "arena_select_character_absent" => run => run.SelectAbsent(me, BotFighters.All, Rules, random, now),
            "arena_player_shop_closed" => run => run.ShopClosed(me, round, Number(body["ShopPhaseDetails"]?["NumRerolls"]), Transactions(body["ShopPhaseDetails"]?["ItemTransactions"]),
                Rules, botMatches, random, now),
            "arena_shop_absent" => run => run.ShopAbsent(me, round, Rules, botMatches, random, now),
            "arena_checkin" or "arena_match_absent" => run => run.Checkin(me, round, Rules, random, now),
            _ => null,
        };
        if (transition is null || (Text(body["ArenaLobbyId"]) ?? Text(body["ArenaParentId"])) is not { } runId)
        {
            log.LogInformation("{Call} from {Player}: not acted on (answered success)", call, me);
            return new(ArenaState.Answer(true, me));
        }

        var (run, step) = await ArenaRunStore.UpdateAsync(redis, runId, transition);
        if (run is null)
        {
            log.LogWarning("{Call} from {Player}: no Arena {Run}; refused", call, me, runId);
            return new(ArenaState.Answer(false, me));
        }

        if (step.Accepted)
        {
            log.LogInformation("Arena {Run} round {Round}: {Call} from {Player} taken; now {Phase}", run.Id, run.Round, call, me, run.Phase);
            await CarryOutAsync(redis, run, step.Effects, ct);
        }
        else
        {
            log.LogInformation("Arena {Run} round {Round} ({Phase}): {Call} from {Player} refused", run.Id, run.Round, run.Phase, call, me);
        }

        return call switch
        {
            "arena_reroll_characters" when run.Players.TryGetValue(me, out var player) => new(ArenaState.RerollAnswer(step.Accepted, player, Rules)),
            // A check-in is answered success whether it was the first or not: the game only waits on it.
            "arena_checkin" => new(ArenaState.Answer(true, me)),
            // A player whose Arena has ended is told so again: their game missed the notification (or reports first).
            "arena_match_absent" when run.Players.TryGetValue(me, out var player) && !player.IsBot && !run.Teams[player.Team].In =>
                new(ArenaState.PlayerEnd(run, me, Rules), ArenaAnswer.PlayerEnded),
            "arena_match_absent" => new(ArenaState.Answer(true, me), run.Awaits(me, round) ? ArenaAnswer.KeepWaiting : 0),
            _ => new(ArenaState.Answer(step.Accepted, me)),
        };
    }

    public async Task MatchEndedAsync(string runId, string matchId, int? winnerSide, CancellationToken ct)
    {
        var redis = Redis();
        if (await ArenaRunStore.GetAsync(redis, runId) is not { } current)
        {
            log.LogWarning("Arena match {Match} ended, but its Arena {Run} is gone", matchId, runId);
            return;
        }

        var connected = await ConnectedAsync(redis, current);
        ArenaRunPairing? pairing = null;
        var (run, step) = await ArenaRunStore.UpdateAsync(redis, runId, r =>
        {
            pairing = r.Pairings.FirstOrDefault(p => p.MatchId == matchId);
            return r.MatchResult(matchId, winnerSide, connected, Rules, Random.Shared, time.GetUtcNow().ToUnixTimeMilliseconds());
        });
        if (run is null || pairing is null || !step.Accepted)
        {
            log.LogInformation("Arena {Run}: the end of match {Match} (winner side {Side}) not taken: decided already, or not this round's", runId, matchId,
                winnerSide?.ToString() ?? "none");
            return;
        }

        log.LogInformation("Arena {Run}: match {Match} ended, reports' winner side {Side}: {Outcome}; now round {Round} {Phase}", runId, matchId,
            winnerSide?.ToString() ?? "none", Outcome(run, pairing), run.Round, run.Phase);
        await CarryOutAsync(redis, run, step.Effects, ct);
    }

    public async Task DeadlinesAsync(CancellationToken ct)
    {
        var redis = Redis();
        long now = time.GetUtcNow().ToUnixTimeMilliseconds();
        foreach (string runId in await ArenaRunStore.DueAsync(redis, now))
        {
            var connected = await ArenaRunStore.GetAsync(redis, runId) is { } current ? await ConnectedAsync(redis, current) : new HashSet<string>();
            var (run, step) = await ArenaRunStore.UpdateAsync(redis, runId, r => r.Tick(now, BotFighters.All, Rules, botMatches, Random.Shared, connected));
            if (run is null)
            {
                await ArenaRunStore.ForgetDeadlineAsync(redis, runId);
                log.LogInformation("Arena {Run} is gone; its deadline is forgotten", runId);
            }
            else if (step.Accepted)
            {
                log.LogInformation("Arena {Run} round {Round}: the deadline passed; now {Phase}", run.Id, run.Round, run.Phase);
                await CarryOutAsync(redis, run, step.Effects, ct);
            }
        }
    }

    // ItemTransactions [{bPurchase, LocalShopIndex, ItemIndex}]: a purchase names a shop page and an offer on it, a sale an
    // inventory slot.
    private static List<ArenaTransaction> Transactions(JsonNode? node) => [.. (node as JsonArray ?? []).OfType<JsonObject>()
        .Select(t => new ArenaTransaction(t["bPurchase"] is JsonValue b && b.TryGetValue(out bool purchase) && purchase, Number(t["LocalShopIndex"]), Number(t["ItemIndex"])))];

    private static int Number(JsonNode? node) => node is JsonValue v && v.TryGetValue(out int n) ? n : 0;

    // The run's players whose games are connected (their websocket is up: online_players).
    private static async Task<IReadOnlySet<string>> ConnectedAsync(IDatabase redis, ArenaRun run)
    {
        var connected = new HashSet<string>();
        foreach (var player in run.Players.Values.Where(p => !p.IsBot))
        {
            if (await redis.SetContainsAsync("online_players", player.Id))
            {
                connected.Add(player.Id);
            }
        }

        return connected;
    }

    // How a pairing was decided, for the log: each team's health now, and the winner.
    private static string Outcome(ArenaRun run, ArenaRunPairing pairing)
    {
        string Team(int team) => team == ArenaRun.Mirror ? "the mirror" : $"team {team} ({run.Teams[team].Life} health{(run.Teams[team].In && run.Teams[team].Life > 0 ? "" : ", out")})";
        string winner = pairing.Winner switch
        {
            ArenaRun.BothLost => "both lost",
            { } w => $"{Team(w)} won",
            null => "undecided",
        };
        return $"{Team(pairing.A)} against {Team(pairing.B)}: {winner}";
    }

    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue(out string? s) && s.Length > 0 ? s : null;

    // What a transition asked for (see the header), in its order.
    private async Task CarryOutAsync(IDatabase redis, ArenaRun run, IReadOnlyList<ArenaEffect> effects, CancellationToken ct)
    {
        var launched = new HashSet<string>();
        foreach (var effect in effects)
        {
            switch (effect)
            {
                case LaunchPairing launch:
                    launched.UnionWith(await LaunchAsync(redis, run, launch.Pairing, ct));
                    break;
                case SendPhaseConfigs:
                    await SendPhaseConfigsAsync(redis, run, launched);
                    break;
                case TeamsEnded ended:
                    foreach (var player in run.Players.Values.Where(p => !p.IsBot && ended.Teams.Contains(p.Team)))
                    {
                        await ProfileNotifications.SendAsync(redis, player.Id, ArenaState.PlayerEnd(run, player.Id, Rules));
                        log.LogInformation("Arena {Run} round {Round}: {Player}'s Arena has ended (team {Team}, rank {Rank}); told", run.Id, run.Round,
                            player.Id, player.Team, run.Teams[player.Team].FinalRank);
                    }

                    break;
                case ArenaFinished:
                    log.LogInformation("Arena {Run} finished after round {Round}: {Ranks}", run.Id, run.Round,
                        string.Join(", ", run.Teams.OrderBy(t => t.FinalRank).Select(t => $"{t.FinalRank}. team {t.Index} ({string.Join(",", t.Members)})")));
                    break;
            }
        }
    }

    // A pairing's 2v2 on a node (see the header); the players in it.
    private async Task<IReadOnlyList<string>> LaunchAsync(IDatabase redis, ArenaRun run, int index, CancellationToken ct)
    {
        var pairing = run.Pairings[index];
        var sides = new List<IReadOnlyList<string>> { run.Teams[pairing.A].Members };
        if (pairing.B == ArenaRun.Mirror)
        {
            var mirror = new List<string>();
            foreach (string member in run.Teams[pairing.A].Members)
            {
                var copied = run.Players[member];
                var fighter = copied.Character is { Length: > 0 } c ? (c, copied.Skin ?? "") : BotFighters.Pick(1, Random.Shared)[0];
                mirror.Add(await MatchmakingRequestService.BotAsync(redis, fighter));
            }

            sides.Add(mirror);
        }
        else
        {
            sides.Add(run.Teams[pairing.B].Members);
        }

        var humans = sides.SelectMany(s => s).Where(pid => run.Players.TryGetValue(pid, out var p) && !p.IsBot).ToList();
        string host = humans[0];
        var players = new List<MatchPlayer>();
        for (int side = 0; side < 2; side++)
        {
            for (int i = 0; i < sides[side].Count; i++)
            {
                string pid = sides[side][i];
                if (!humans.Contains(pid))
                {
                    players.Add(new MatchPlayer(pid, i * 2 + side, side, IsHost: false, "", IsBot: true));
                    continue;
                }

                var loadout = await HashAsync(redis, $"player:{pid}");
                var player = run.Players[pid];
                if (Or(player.Character, Get(loadout, "character")) is { Length: > 0 } character && Or(player.Skin, Get(loadout, "skin")) is { Length: > 0 } skin)
                {
                    await PlayedLoadout.RecordAsync(redis, pid, character, skin, Get(loadout, "profileIcon"));
                }

                players.Add(new MatchPlayer(pid, i * 2 + side, side, IsHost: pid == host, Get(loadout, "ip") ?? "", IsBot: false));
            }
        }

        // Named an Arena's before it exists, so that its end is the run's whatever happens to the launch.
        await ArenaResults.NameMatchAsync(redis, pairing.MatchId!, run.Id);
        if (run.Round > 1)
        {
            foreach (string pid in humans)
            {
                await ProfileNotifications.SendAsync(redis, pid, ArenaState.NextMatch(pairing.MatchId!));
            }
        }

        var configOverride = BotDefaults.UnrankedConfigOverride();
        foreach (var (key, value) in ArenaState.ModeInfo(Rules.FaceoffWaitSeconds, Rules.ShopSeconds, readyToStart: false))
        {
            configOverride[key] = value?.DeepClone();
        }

        configOverride["TeamData"] = ArenaState.NoScoreTarget();
        configOverride["RiftNodeAttunement"] = ArenaState.GemAttunement;

        var fields = BotDefaults.UnrankedNotificationFields();
        fields["customMatchTime"] = Rules.RoundSeconds;
        // Timed, not a stock match: no ring-out target (0; PROBE: whether the game reads 0 as none is not known yet).
        fields["customNumRingouts"] = 0;
        var launched = await launcher.LaunchAsync(new MatchLaunch("2v2", MatchmakingMaps.Pick("2v2", "casual", log), "2v2", players,
            GameplayConfigOverride: configOverride, ConfigData: ArenaState.ConfigData(run, host, Rules), BotPerks: BotDefaults.PerksArray(),
            NotificationFields: fields, MatchId: pairing.MatchId,
            PlayerConfigData: humans.ToDictionary(pid => pid, pid => ArenaState.ConfigData(run, pid, Rules))), ct);
        if (launched is null)
        {
            log.LogError("Arena {Run} round {Round}: match {Match}, team {A} against {B}, was not started", run.Id, run.Round, pairing.MatchId, pairing.A, pairing.B);
            return [];
        }

        log.LogInformation("Arena {Run} round {Round}: match {Match}, team {A} ({TeamA}) against {B} ({TeamB})", run.Id, run.Round, pairing.MatchId,
            pairing.A, string.Join(",", sides[0]), pairing.B, string.Join(",", sides[1]));
        return humans;
    }

    // Each player still in their kept config with the run as it is now (see the header).
    private async Task SendPhaseConfigsAsync(IDatabase redis, ArenaRun run, IReadOnlySet<string> launched)
    {
        foreach (var player in run.PlayersIn.Where(p => !launched.Contains(p.Id)))
        {
            var pairing = run.Pairings.FirstOrDefault(p => p.A == player.Team || p.B == player.Team);
            var kept = await redis.StringGetAsync(GameplayConfigs.Key(player.Id));
            if (pairing?.MatchId is not { } matchId || !kept.HasValue || Js.Parse(kept.ToString()) is not JsonObject message
                || (string?)message["payload"]?["match"]?["id"] != matchId || message["data"] is not JsonObject data)
            {
                log.LogError("Arena {Run} round {Round} ({Phase}): {Player} holds no config of their match {Match}; nothing sent", run.Id, run.Round, run.Phase,
                    player.Id, pairing?.MatchId);
                continue;
            }

            foreach (var (key, value) in ArenaState.ConfigData(run, player.Id, Rules))
            {
                data[key] = value?.DeepClone();
            }

            if (data["GameplayConfig"]?["Players"] is JsonObject entries)
            {
                foreach (var (id, entry) in entries)
                {
                    if (entry is not JsonObject fields || !run.Players.TryGetValue(id, out var participant))
                    {
                        continue;
                    }

                    if (participant.Character is { Length: > 0 } character)
                    {
                        fields["Character"] = character;
                        fields["Skin"] = participant.Skin ?? "";
                    }

                    // Everyone's items in everyone's copy: each game of a P2P match runs the match itself.
                    fields["Gems"] = ArenaState.Gems(participant);
                }
            }

            if (run.Phase == ArenaPhase.Match && data["GameplayConfig"] is JsonObject config)
            {
                if (config["ArenaModeInfo"] is not JsonObject modeInfo)
                {
                    config["ArenaModeInfo"] = modeInfo = ArenaState.ModeInfo(Rules.FaceoffWaitSeconds, Rules.ShopSeconds, readyToStart: true)["ArenaModeInfo"]!.DeepClone().AsObject();
                }

                modeInfo["bReadyToStart"] = true;
                // No shop time left: the matchup screen restarts its pre-match steps whenever it is shown again (after
                // Arena Status, 0x142a52db0), and the shop's entry (0x142a566f0) closes at once on a ShopTime of 0 instead
                // of opening a second shop (bench 2026-10-11).
                modeInfo["ShopTime"] = 0;
            }

            if (player.Character is { Length: > 0 } picked)
            {
                await PlayedLoadout.RecordAsync(redis, player.Id, picked, player.Skin ?? "");
            }

            await redis.StringSetAsync(GameplayConfigs.Key(player.Id), Js.Stringify(message), expiry: null, keepTtl: true);
            await PlayerMessages.SendAsync(redis, [player.Id], message);
        }
    }

    private static async Task<Dictionary<string, string>> HashAsync(IDatabase redis, string key) =>
        (await redis.HashGetAllAsync(key)).ToDictionary(e => e.Name.ToString(), e => e.Value.ToString());

    private static string? Get(Dictionary<string, string> hash, string field) => hash.TryGetValue(field, out var value) ? value : null;

    private static string Or(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? "";
}

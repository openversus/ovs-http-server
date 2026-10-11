using System.Text.Json.Nodes;

namespace OpenVersus.Server.Core.Arenas;

// POST /matches/matchmaking/arena-retail/request: the Arena lobby's "Start Matchmaking" (the request's match is the Arena
// lobby; only its leader has the button). Its leader is answered with a ticket for the whole lobby, queued on the arena
// list (MatchmakingRequestService), which no worker matches yet: the game's own timer (Arenas:MatchmakingBotTimeoutSeconds,
// give or take 10 s) cancels the ticket and starts the Arena with bots (PUT /ssc/invoke/start_arena_direct). Anyone else
// is refused as a closed queue is (FfaSchedule, TestingGrounds), a Hydra action failure, so the log names it. A refused
// game waits all the same, every button disabled until it is disconnected: with the TS catch-all answer (no ticket;
// bench, 2026-09-30 and 2026-10-10) and with this one (2026-10-10).

/// <summary>The Arena queue's matchmaking request.</summary>
public static class ArenaMatchmaking
{
    /// <summary>The criteria the Arena lobby sends: the queue's MatchmakingCriteriaSlug (arena) with the retail suffix; the bare slug is taken too.</summary>
    public const string Criteria = "arena-retail", BareCriteria = "arena";

    /// <summary>The Arena tickets' list (their matchType); no worker matches it yet.</summary>
    public const string List = "arena";

    /// <summary>The answer to an Arena matchmaking request from anyone but an Arena lobby's leader: a Hydra action failure, HTTP 200, as a closed queue's.</summary>
    public static JsonObject Unavailable() => new()
    {
        ["body"] = new JsonObject
        {
            ["error"] = "arena_queue_unavailable",
            ["ErrorCode"] = "QueueClosed",
            ["ErrorMessage"] = "Arena matchmaking is not open.",
        },
        ["metadata"] = null,
        ["return_code"] = 1,
    };
}

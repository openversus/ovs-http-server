using System.Text.Json.Nodes;

namespace OpenVersus.Server.Core.Arenas;

// POST /matches/matchmaking/arena-retail/request: the Arena lobby's "Start Matchmaking" (the request's match is the Arena
// lobby). No Arena queue answers it with a ticket: it is refused as a closed queue is (FfaSchedule, TestingGrounds), a
// Hydra action failure, so the log names it. The game waits all the same, every button disabled until it is
// disconnected: with the TS catch-all answer (no ticket; bench, 2026-09-30 and 2026-10-10) and with this one (2026-10-10).

/// <summary>The Arena queue's matchmaking request.</summary>
public static class ArenaMatchmaking
{
    /// <summary>The criteria the Arena lobby sends: the queue's MatchmakingCriteriaSlug (arena) with the retail suffix; the bare slug is taken too.</summary>
    public const string Criteria = "arena-retail", BareCriteria = "arena";

    /// <summary>The answer to every Arena matchmaking request: a Hydra action failure, HTTP 200, as a closed queue's.</summary>
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

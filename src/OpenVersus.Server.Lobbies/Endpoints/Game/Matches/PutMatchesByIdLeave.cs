using System.Text.Json.Nodes;
using FastEndpoints;
using OpenVersus.Server.Core.Matches;
using OpenVersus.Server.Http.Shared.Endpoints;
using OpenVersus.Server.Http.Shared.Hosting;
using StackExchange.Redis;

namespace OpenVersus.Server.Lobbies.Endpoints.Game.Matches;

/// <summary>
/// PUT /matches/{id}/leave: the game saying its player is leaving a match. The leave goes on the match flow's results
/// stream (<see cref="MatchLeaves"/>), where it is settled as the game's websocket close is, once, however many signals
/// of the same leave arrive. Answered as the TS server answers it, {body: {}, metadata: null, return_code: 200} (the TS
/// handler rated the leaver's team as the loss itself; the match flow does that here, under the rated-matches rule).
/// Seen in: binary 0x144fd9f20; TS server: PUT /matches/{id}/leave.
/// </summary>
public sealed class PutMatchesByIdLeave : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/matches/{id}/leave");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        string matchId = Route<string>("id") ?? "";
        string? playerId = HttpContext.Session()?.AccountId;
        if (playerId is { Length: > 0 } && matchId.Length > 0 && TryResolve<IConnectionMultiplexer>()?.GetDatabase() is { } redis)
        {
            await MatchLeaves.AppendAsync(redis, matchId, playerId);
            Logger.LogInformation("Player {Account} is leaving match {MatchId}: put to the match flow", playerId, matchId);
        }
        else
        {
            Logger.LogWarning("Leave of match {MatchId} by {Account} not recorded: {Why}", matchId, playerId ?? "(no session)",
                playerId is null ? "no session" : matchId.Length == 0 ? "no match id" : "no Redis");
        }

        await SendJsonAsync(new JsonObject { ["body"] = new JsonObject(), ["metadata"] = null, ["return_code"] = 200 }, ct);
    }
}

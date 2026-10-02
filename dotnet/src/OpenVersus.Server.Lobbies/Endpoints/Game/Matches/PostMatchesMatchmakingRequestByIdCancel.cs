using FastEndpoints;
using OpenVersus.Server.Core.Matches;
using OpenVersus.Server.Http.Shared.Endpoints;
using OpenVersus.Server.Http.Shared.Hosting;
using StackExchange.Redis;

namespace OpenVersus.Server.Lobbies.Endpoints.Game.Matches;

/// <summary>
/// POST /matches/matchmaking/request/{id}/cancel: request {id} out of the queue for the player's whole lobby
/// (<see cref="IMatchmakingRequestService.CancelAsync"/>).
/// Seen in: binary 0x144fd78f0; captured 5x; TS server: POST /matches/matchmaking/request/{id}/cancel.
/// </summary>
public sealed class PostMatchesMatchmakingRequestByIdCancel : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.POST);
        Routes("/matches/matchmaking/request/{id}/cancel");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (HttpContext.Session() is not { } session || TryResolve<IConnectionMultiplexer>() is null)
        {
            Logger.LogWarning("matchmaking cancel with no session or no Redis; answered empty");
            await SendJsonAsync(new System.Text.Json.Nodes.JsonObject { ["body"] = new System.Text.Json.Nodes.JsonObject(), ["metadata"] = null, ["return_code"] = 0 }, ct);
            return;
        }

        var request = new PartyRequest(session.AccountId, session.Claims, ClientAddress.Of(HttpContext, stripMapped: true), null);
        await SendJsonAsync(await Resolve<IMatchmakingRequestService>().CancelAsync(Route<string>("id") ?? "", request, ct), ct);
    }
}

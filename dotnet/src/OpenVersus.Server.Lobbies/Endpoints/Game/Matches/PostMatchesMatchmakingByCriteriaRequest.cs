using FastEndpoints;
using OpenVersus.Server.Core.Matches;
using OpenVersus.Server.Http.Shared.Endpoints;
using OpenVersus.Server.Http.Shared.Hosting;
using StackExchange.Redis;

namespace OpenVersus.Server.Lobbies.Endpoints.Game.Matches;

/// <summary>
/// POST /matches/matchmaking/{criteria}/request: the player (and their lobby, for 2v2) into the matchmaking queue
/// (<see cref="IMatchmakingRequestService"/>): 1v1-retail, ranked-1v1-retail, 2v2-retail, casual-retail and ffa; any
/// other criteria as a stub (the router sends only those here). The answer is sent before the ticket is published, as the
/// TS server does.
/// Seen in: binary 0x144fdd630; captured 10x; TS server: POST /matches/matchmaking/1v1-retail/request, POST /matches/matchmaking/2v2-retail/request, POST /matches/matchmaking/ranked-1v1-retail/request, POST /matches/matchmaking/ffa/request.
/// </summary>
public sealed class PostMatchesMatchmakingByCriteriaRequest : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.POST);
        Routes("/matches/matchmaking/{criteria}/request");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (HttpContext.Session() is not { } session || TryResolve<IConnectionMultiplexer>() is null)
        {
            Logger.LogWarning("matchmaking request with no session or no Redis; not answered");
            await Send.ResultAsync(Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
            return;
        }

        var request = new PartyRequest(session.AccountId, session.Claims, ClientAddress.Of(HttpContext, stripMapped: true), (await ReadBodyAsync(ct)) as System.Text.Json.Nodes.JsonObject);
        if (await Resolve<IMatchmakingRequestService>().RequestAsync(Route<string>("criteria") ?? "", request, ct) is not { } answer)
        {
            await SendNotPortedAsync();
            return;
        }

        await SendJsonAsync(answer.Body, answer.Status, ct);
        if (answer.After is { } after)
        {
            // Once the answer has gone (a Hydra one is encoded after this endpoint returns, so completing the response here
            // would not send it): the game has the answer before the ticket reaches the queue.
            HttpContext.Response.OnCompleted(after);
        }
    }
}

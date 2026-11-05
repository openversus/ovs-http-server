using FastEndpoints;
using OpenVersus.Server.Core.Hosting;
using OpenVersus.Server.Core.Matches;
using OpenVersus.Server.Http.Shared.Endpoints;
using OpenVersus.Server.Http.Shared.Hosting;
using StackExchange.Redis;

namespace OpenVersus.Server.Lobbies.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/casual_queue: the game waited 45-65 s in the Casual queue, cancelled its ticket, and asks for a match
/// against bots (<see cref="IMatchmakingRequestService.CasualBotsAsync"/>).
/// Seen in: binary ssc name. The TS server does not answer it (its catch-all: the game then waited forever).
/// </summary>
public sealed class PutCasualQueue : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/casual_queue");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (HttpContext.Session() is not { } session || TryResolve<IConnectionMultiplexer>() is null)
        {
            Logger.LogWarning("casual_queue with no session or no Redis; not answered");
            await Send.ResultAsync(Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
            return;
        }

        var request = new PartyRequest(session.AccountId, session.Claims, ClientAddress.Of(HttpContext, stripMapped: true), null);
        await SendJsonAsync(await Resolve<IMatchmakingRequestService>().CasualBotsAsync(request, ct), ct);
    }
}

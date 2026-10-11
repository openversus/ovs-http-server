using FastEndpoints;
using Microsoft.Extensions.Logging;
using OpenVersus.Server.Core.Hosting;
using OpenVersus.Server.Core.Matches;
using OpenVersus.Server.Http.Shared.Endpoints;
using OpenVersus.Server.Http.Shared.Hosting;
using StackExchange.Redis;

namespace OpenVersus.Server.Lobbies.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/start_arena_direct: the Arena lobby's matchmaking timer ran out (Arenas:MatchmakingBotTimeoutSeconds);
/// the game cancelled its ticket and asks for the Arena to start with bots
/// (<see cref="IMatchmakingRequestService.ArenaBotsAsync"/>). Body: {ArenaLobbyId, MatchId (the same id), Mode "",
/// MultiplayParams}. Seen in: bench (2026-10-11); the game shows "Match Found!" after it. The TS server does not answer it.
/// </summary>
public sealed class PutStartArenaDirect : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/start_arena_direct");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var body = (await ReadBodyAsync(ct))?.AsObject();
        if (HttpContext.Session() is not { } session || TryResolve<IConnectionMultiplexer>() is null)
        {
            Logger.LogWarning("start_arena_direct with no session or no Redis; not answered");
            await Send.ResultAsync(Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
            return;
        }

        Logger.LogInformation("start_arena_direct from {Player}: ArenaLobbyId {Lobby}", session.AccountId, body?["ArenaLobbyId"]?.ToJsonString() ?? "none");
        // The whole request, for reading what the game sends (it carries account ids: Debug, off unless Log:Level is raised).
        Logger.LogDebug("start_arena_direct body: {Body}", body?.ToJsonString() ?? "none");
        var request = new PartyRequest(session.AccountId, session.Claims, ClientAddress.Of(HttpContext, stripMapped: true), body);
        await SendJsonAsync(await Resolve<IMatchmakingRequestService>().ArenaBotsAsync(request, ct), ct);
    }
}

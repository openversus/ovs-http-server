using FastEndpoints;
using OpenVersus.Server.Core.Matches;
using OpenVersus.Server.Http.Shared.Endpoints;
using OpenVersus.Server.Http.Shared.Hosting;
using StackExchange.Redis;

namespace OpenVersus.Server.Lobbies.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/rematch_decline: the player declines the rematch on the post-match screen; everyone goes back to the
/// menus (<see cref="IRematches.DeclineAsync"/>). Answered {body: []}, as the TS server always answered it.
/// Seen in: binary ssc name; TS server: PUT /ssc/invoke/rematch_decline.
/// </summary>
public sealed class PutRematchDecline : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/rematch_decline");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (HttpContext.Session()?.AccountId is { Length: > 0 } playerId)
        {
            try
            {
                await Resolve<IRematches>().DeclineAsync(playerId, ct);
            }
            catch (Exception e) when (e is RedisException or TimeoutException or InvalidOperationException or System.Text.Json.JsonException)
            {
                Logger.LogError("rematch_decline from {Player}: {Error}", playerId, e.Message);
            }
        }

        await SendJsonAsync(RematchAnswer.Body(), ct);
    }
}

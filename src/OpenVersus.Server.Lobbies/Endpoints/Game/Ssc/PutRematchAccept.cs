using System.Text.Json.Nodes;
using FastEndpoints;
using OpenVersus.Server.Core.Matches;
using OpenVersus.Server.Http.Shared.Endpoints;
using OpenVersus.Server.Http.Shared.Hosting;
using StackExchange.Redis;

namespace OpenVersus.Server.Lobbies.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/rematch_accept: the player accepts the rematch on the post-match screen of a custom lobby's match or
/// a Casual match (<see cref="IRematches.AcceptAsync"/>). Answered {body: []}, as the TS server always answered it.
/// Seen in: binary ssc name; TS server: PUT /ssc/invoke/rematch_accept.
/// </summary>
public sealed class PutRematchAccept : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/rematch_accept");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (HttpContext.Session()?.AccountId is { Length: > 0 } playerId)
        {
            try
            {
                await Resolve<IRematches>().AcceptAsync(playerId, ct);
            }
            catch (Exception e) when (e is RedisException or TimeoutException or InvalidOperationException or System.Text.Json.JsonException)
            {
                Logger.LogError("rematch_accept from {Player}: {Error}", playerId, e.Message);
            }
        }

        await SendJsonAsync(RematchAnswer.Body(), ct);
    }
}

/// <summary>What the TS server answered both rematch routes, whatever happened.</summary>
internal static class RematchAnswer
{
    public static JsonObject Body() => new() { ["body"] = new JsonArray(), ["metadata"] = null, ["return_code"] = 0 };
}

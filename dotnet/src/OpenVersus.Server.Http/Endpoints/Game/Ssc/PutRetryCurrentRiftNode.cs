using System.Text.Json.Nodes;
using FastEndpoints;
using MongoDB.Driver;
using OpenVersus.Server.Core.Rifts;
using OpenVersus.Server.Http.Hosting;
using StackExchange.Redis;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/retry_current_rift_node: "Retry" on a rift match's results screen; the node is started again and the
/// match reaches the game over the websocket (<see cref="IRiftMatchService.RetryNodeAsync"/>); 503 when the stores cannot
/// be reached.
/// Seen in: binary ssc name; bench (proxy log, 2026-09-30). The TS server does not answer it.
/// </summary>
public sealed class PutRetryCurrentRiftNode : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/retry_current_rift_node");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        JsonNode answer;
        try
        {
            answer = await Resolve<IRiftMatchService>().RetryNodeAsync(HttpContext.Session()?.Claims, (await ReadBodyAsync(ct))?.AsObject(), ct);
        }
        catch (Exception e) when (e is InvalidOperationException or MongoException or RedisException or TimeoutException)
        {
            // The stores cannot be reached.
            Logger.LogError("retry_current_rift_node: {Error}", e.Message);
            await Send.ResultAsync(Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
            return;
        }

        await SendJsonAsync(answer, ct);
    }
}

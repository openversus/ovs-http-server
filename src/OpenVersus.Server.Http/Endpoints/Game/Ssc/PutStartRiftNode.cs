using System.Text.Json.Nodes;
using FastEndpoints;
using MongoDB.Driver;
using OpenVersus.Server.Core.Rifts;
using OpenVersus.Server.Http.Shared.Endpoints;
using OpenVersus.Server.Http.Shared.Hosting;
using StackExchange.Redis;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/start_rift_node: the player starts a match node of the rift; the match reaches the game over the
/// websocket (<see cref="IRiftMatchService.StartNodeAsync"/>); 503 when the stores cannot be reached.
/// Seen in: binary ssc name; capture (bench, 2026-09-30). The TS server does not answer it.
/// </summary>
public sealed class PutStartRiftNode : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/start_rift_node");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        JsonNode answer;
        try
        {
            answer = await Resolve<IRiftMatchService>().StartNodeAsync(HttpContext.Session()?.Claims, (await ReadBodyAsync(ct))?.AsObject(), ct);
        }
        catch (Exception e) when (e is InvalidOperationException or MongoException or RedisException or TimeoutException)
        {
            // The stores cannot be reached.
            Logger.LogError("start_rift_node: {Error}", e.Message);
            await Send.ResultAsync(Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
            return;
        }

        await SendJsonAsync(answer, ct);
    }
}

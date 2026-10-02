using System.Text.Json.Nodes;
using FastEndpoints;
using MongoDB.Driver;
using OpenVersus.Server.Core.Rifts;
using OpenVersus.Server.Http.Shared.Hosting;
using StackExchange.Redis;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// GET /ssc/invoke/get_or_create_rift_state: the player's rift state (<see cref="IRiftStateService"/>), which the rift
/// select page waits for; 503 when the stores cannot be reached.
/// Seen in: binary ssc name; capture (bench, 2026-09-30). The TS server does not answer it.
/// </summary>
public sealed class GetGetOrCreateRiftState : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/ssc/invoke/get_or_create_rift_state");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        JsonNode answer;
        try
        {
            answer = await Resolve<IRiftStateService>().GetOrCreateAsync(HttpContext.Session()?.Claims, ct);
        }
        catch (Exception e) when (e is InvalidOperationException or MongoException or RedisException or TimeoutException)
        {
            // The stores cannot be reached.
            Logger.LogError("Rift state: {Error}", e.Message);
            await Send.ResultAsync(Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
            return;
        }

        await SendJsonAsync(answer, ct);
    }
}

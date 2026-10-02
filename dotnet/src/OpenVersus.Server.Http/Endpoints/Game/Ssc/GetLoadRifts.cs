using System.Text.Json.Nodes;
using FastEndpoints;
using MongoDB.Driver;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Hydra;
using OpenVersus.Server.Core.Rifts;
using OpenVersus.Server.Core.Static;
using OpenVersus.Server.Http.Shared.Endpoints;
using OpenVersus.Server.Http.Shared.Hosting;
using StackExchange.Redis;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// GET /ssc/invoke/load_rifts: the rift configurations, the same for everyone (<see cref="RiftCatalog"/>: the TS server's,
/// the hiss-only rifts added, end times moved), and the player's own runtime data (<see cref="IRiftProgressService"/>). In the login
/// batch. With no session, the TS server's answer as it is; 503 when the stores cannot be reached.
/// Seen in: binary ssc name; TS server: GET /ssc/invoke/load_rifts.
/// </summary>
public sealed class GetLoadRifts : JsonBodyEndpoint
{
    // The configurations are 3 MB of the answer: kept as JSON text and as Hydra, and spliced around each player's data.
    private static readonly Lazy<JsonNode> s_configs = new(() => RiftCatalog.Configs);
    private static readonly Lazy<string> s_configsJson = new(() => Js.Stringify(s_configs.Value));
    private static readonly Lazy<byte[]> s_configsHydra = new(() => HydraEncoder.Encode(s_configs.Value));

    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/ssc/invoke/load_rifts");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (HttpContext.Session()?.AccountId is not { Length: > 0 } playerId)
        {
            await SendStaticAsync("ssc-load-rifts", ct);
            return;
        }

        JsonObject dynamic, player;
        try
        {
            (dynamic, player) = await Resolve<IRiftProgressService>().InstanceAsync(playerId, ct);
        }
        catch (Exception e) when (e is InvalidOperationException or MongoException or RedisException or TimeoutException)
        {
            // The stores cannot be reached.
            Logger.LogError("load_rifts: {Error}", e.Message);
            await Send.ResultAsync(Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
            return;
        }

        if (HydraBodies.IsHydra(HttpContext))
        {
            await HydraBodies.WriteAsync(HttpContext, new JsonObject
            {
                ["body"] = new JsonObject
                {
                    ["RiftConfigs"] = HydraRaw.Node(s_configsHydra.Value),
                    ["DynamicInstanceRuntimeData"] = dynamic,
                    ["PlayerInstanceRuntimeData"] = player,
                },
                ["metadata"] = null,
                ["return_code"] = 0,
            }, ct);
            return;
        }

        await Send.StringAsync(
            $"{{\"body\":{{\"RiftConfigs\":{s_configsJson.Value},\"DynamicInstanceRuntimeData\":{Js.Stringify(dynamic)},\"PlayerInstanceRuntimeData\":{Js.Stringify(player)}}},\"metadata\":null,\"return_code\":0}}",
            contentType: "application/json; charset=utf-8", cancellation: ct);
    }
}

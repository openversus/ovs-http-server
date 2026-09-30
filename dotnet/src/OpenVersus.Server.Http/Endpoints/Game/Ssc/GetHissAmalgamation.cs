using FastEndpoints;
using MongoDB.Driver;
using OpenVersus.Server.Core.Hiss;
using OpenVersus.Server.Http.Hosting;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// GET /ssc/invoke/hiss_amalgamation: the game's configuration (<see cref="IHissService"/>), as PUT answers it. The TS
/// server answers this path too (server.ts, "Missing Crc").
/// Seen in: binary ssc name; TS server: GET /ssc/invoke/hiss_amalgamation.
/// </summary>
public sealed class GetHissAmalgamation : HissAmalgamationEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/ssc/invoke/hiss_amalgamation");
    }
}

/// <summary>Sends the hiss answer for the current CRC: its Hydra encoding (made once per CRC) or its JSON; 503 when the stores cannot be reached.</summary>
public abstract class HissAmalgamationEndpoint : EndpointWithoutRequest
{
    public override async Task HandleAsync(CancellationToken ct)
    {
        HissAnswer answer;
        try
        {
            answer = await Resolve<IHissService>().AnswerAsync(ct);
        }
        catch (Exception e) when (e is InvalidOperationException or MongoException or TimeoutException)
        {
            Logger.LogError("Hiss amalgamation: {Error}", e.Message);
            await Send.ResultAsync(Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
            return;
        }

        if (HydraBodies.IsHydra(HttpContext))
        {
            await HydraBodies.WriteEncodedAsync(HttpContext, answer.Hydra, ct);
        }
        else
        {
            await Send.StringAsync(answer.Json, contentType: "application/json; charset=utf-8", cancellation: ct);
        }
    }
}

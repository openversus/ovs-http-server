using FastEndpoints;
using OpenVersus.Server.Core.Hydra;
using OpenVersus.Server.Http.Batch;
using OpenVersus.Server.Http.Shared.Endpoints;
using OpenVersus.Server.Http.Shared.Hosting;

namespace OpenVersus.Server.Http.Endpoints.Game.Batch;

/// <summary>
/// PUT /batch: runs sub-requests (<see cref="BatchRunner"/>). A body without a requests array answers 400 (the TS
/// server throws and never answers).
/// Seen in: binary 0x145054730; captured 26x; TS server: PUT /batch.
/// </summary>
public sealed class PutBatch : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/batch");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        byte[]? answer = await Resolve<BatchRunner>().RunAsync(HttpContext, await ReadBodyAsync(ct), ct);
        if (answer is null)
        {
            await Send.ResultAsync(Results.StatusCode(StatusCodes.Status400BadRequest));
        }
        else if (HydraBodies.IsHydra(HttpContext))
        {
            await HydraBodies.WriteEncodedAsync(HttpContext, answer, ct);
        }
        else
        {
            await Send.StringAsync(Hydra.DecodeToJson(answer), contentType: "application/json; charset=utf-8", cancellation: ct);
        }
    }
}

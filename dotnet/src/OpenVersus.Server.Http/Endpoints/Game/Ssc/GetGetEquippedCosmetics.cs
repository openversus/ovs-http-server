using System.Text.Json.Nodes;
using FastEndpoints;
using MongoDB.Driver;
using OpenVersus.Server.Core.Cosmetics;
using OpenVersus.Server.Core.Identity;
using OpenVersus.Server.Http.Shared.Hosting;
using StackExchange.Redis;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// GET /ssc/invoke/get_equipped_cosmetics: the player's equipped cosmetics (<see cref="ICosmeticsService"/>), for the
/// player the request resolves to (<see cref="IAccountResolver"/>), else the token's; 503 when the stores cannot be
/// reached. In the login batch.
/// Seen in: binary ssc name; TS server: GET /ssc/invoke/get_equipped_cosmetics.
/// </summary>
public sealed class GetGetEquippedCosmetics : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/ssc/invoke/get_equipped_cosmetics");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        JsonObject cosmetics;
        try
        {
            var resolved = await Resolve<IAccountResolver>().ResolveAsync(AccountLookups.From(HttpContext));
            string accountId = resolved?.Id is { Length: > 0 } id ? id : HttpContext.Session()?.AccountId ?? "";
            cosmetics = await Resolve<ICosmeticsService>().EquippedAsync(accountId, ct);
        }
        catch (Exception e) when (e is InvalidOperationException or MongoException or RedisException or TimeoutException)
        {
            // The stores cannot be reached (the TS server never answers then).
            Logger.LogError("Equipped cosmetics: {Error}", e.Message);
            await Send.ResultAsync(Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
            return;
        }

        await SendJsonAsync(CosmeticsAnswer.Answer(cosmetics), ct);
    }
}

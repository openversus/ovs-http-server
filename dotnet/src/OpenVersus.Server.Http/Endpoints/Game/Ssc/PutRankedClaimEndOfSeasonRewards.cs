using System.Text.Json.Nodes;
using FastEndpoints;
using MongoDB.Driver;
using OpenVersus.Server.Core.Leaderboards;
using OpenVersus.Server.Http.Hosting;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/ranked_claim_end_of_season_rewards: the player got past the end-of-season screen for a season
/// (<see cref="IRankedDataService.ClaimRewardsAsync"/>); ranked_data then says that season's rewards are granted, and
/// the screen is not shown again. Answers as the TS catch-all did.
/// Seen in: binary ssc name; captured 3x (bench, 2026-09-30). The TS server answers it only with its catch-all.
/// </summary>
public sealed class PutRankedClaimEndOfSeasonRewards : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/ranked_claim_end_of_season_rewards");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var body = await ReadBodyAsync(ct) as JsonObject;
        Logger.LogInformation("ranked_claim_end_of_season_rewards body: {Body}", body?.ToJsonString());
        try
        {
            await SendJsonAsync(await Resolve<IRankedDataService>().ClaimRewardsAsync(HttpContext.Session()?.AccountId ?? "", body?["Season"], ct), ct);
        }
        catch (Exception e) when (e is InvalidOperationException or MongoException or TimeoutException)
        {
            Logger.LogError("ranked_claim_end_of_season_rewards: {Error}", e.Message);
            await Send.ResultAsync(Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
        }
    }
}

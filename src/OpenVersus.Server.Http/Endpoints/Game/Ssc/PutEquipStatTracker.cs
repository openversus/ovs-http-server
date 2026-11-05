using System.Text.Json.Nodes;
using FastEndpoints;
using OpenVersus.Server.Core.Cosmetics;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/equip_stat_tracker: one stat tracker slot (<see cref="ICosmeticsService.EquipStatTrackerAsync"/>); the answer echoes the request.
/// Seen in: binary ssc name; TS server: PUT /ssc/invoke/equip_stat_tracker.
/// </summary>
public sealed class PutEquipStatTracker : CosmeticsWriteEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/equip_stat_tracker");
    }

    protected override async Task<JsonNode?> Answer(ICosmeticsService cosmetics, string accountId, JsonObject body, CancellationToken ct) =>
        await cosmetics.EquipStatTrackerAsync(accountId, body["StatTrackerSlotIndex"], body["StatTrackerSlug"], ct) ? body.DeepClone() : null;
}

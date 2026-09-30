using System.Text.Json.Nodes;
using FastEndpoints;
using OpenVersus.Server.Core.Cosmetics;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/equip_banner: the banner (<see cref="ICosmeticsService.EquipAsync"/>); answers {EquippedBanner} with the slug as sent (an empty one is stored as the default).
/// Seen in: binary ssc name; captured 2x; TS server: PUT /ssc/invoke/equip_banner.
/// </summary>
public sealed class PutEquipBanner : CosmeticsWriteEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/equip_banner");
    }

    protected override async Task<JsonNode?> Answer(ICosmeticsService cosmetics, string accountId, JsonObject body, CancellationToken ct) =>
        await cosmetics.EquipAsync(CosmeticSlot.Banner, accountId, body["BannerSlug"], body.ContainsKey("BannerSlug"), ct)
            ? Echo("EquippedBanner", body, "BannerSlug") : null;
}

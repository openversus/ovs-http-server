using System.Text.Json.Nodes;
using FastEndpoints;
using OpenVersus.Server.Core.Cosmetics;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/equip_ringout_vfx: the ring-out effect (<see cref="ICosmeticsService.EquipAsync"/>); answers {EquippedRingoutVfx} with the slug as sent.
/// Seen in: binary ssc name; TS server: PUT /ssc/invoke/equip_ringout_vfx.
/// </summary>
public sealed class PutEquipRingoutVfx : CosmeticsWriteEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/equip_ringout_vfx");
    }

    protected override async Task<JsonNode?> Answer(ICosmeticsService cosmetics, string accountId, JsonObject body, CancellationToken ct) =>
        await cosmetics.EquipAsync(CosmeticSlot.RingoutVfx, accountId, body["RingoutVfxSlug"], body.ContainsKey("RingoutVfxSlug"), ct)
            ? Echo("EquippedRingoutVfx", body, "RingoutVfxSlug") : null;
}

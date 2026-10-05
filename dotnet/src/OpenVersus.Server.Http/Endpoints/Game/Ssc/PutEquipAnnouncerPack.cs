using System.Text.Json.Nodes;
using FastEndpoints;
using OpenVersus.Server.Core.Cosmetics;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/equip_announcer_pack: the announcer pack (<see cref="ICosmeticsService.EquipAsync"/>); answers {EquippedAnnouncerPack}.
/// Seen in: binary ssc name; TS server: PUT /ssc/invoke/equip_announcer_pack.
/// </summary>
public sealed class PutEquipAnnouncerPack : CosmeticsWriteEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/equip_announcer_pack");
    }

    protected override async Task<JsonNode?> Answer(ICosmeticsService cosmetics, string accountId, JsonObject body, CancellationToken ct) =>
        await cosmetics.EquipAsync(CosmeticSlot.AnnouncerPack, accountId, body["AnnouncerPackSlug"], body.ContainsKey("AnnouncerPackSlug"), ct)
            ? Echo("EquippedAnnouncerPack", body, "AnnouncerPackSlug") : null;
}

using System.Text.Json.Nodes;
using FastEndpoints;
using OpenVersus.Server.Core.Cosmetics;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/equip_taunt: one taunt slot of one character (<see cref="ICosmeticsService.EquipTauntAsync"/>); the answer echoes the request.
/// Seen in: binary ssc name; TS server: PUT /ssc/invoke/equip_taunt.
/// </summary>
public sealed class PutEquipTaunt : CosmeticsWriteEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/equip_taunt");
    }

    protected override async Task<JsonNode?> Answer(ICosmeticsService cosmetics, string accountId, JsonObject body, CancellationToken ct) =>
        await cosmetics.EquipTauntAsync(accountId, body["CharacterSlug"], body["TauntSlotIndex"], body["TauntSlug"], ct) ? body.DeepClone() : null;
}

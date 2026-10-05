using System.Text.Json.Nodes;
using FastEndpoints;
using OpenVersus.Server.Core.Cosmetics;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/set_profile_icon: the profile icon, only one the game defines (<see cref="ICosmeticsService.SetProfileIconAsync"/>); answers {EquippedProfileIcon}, {} for an unknown icon.
/// Seen in: binary ssc name; TS server: PUT /ssc/invoke/set_profile_icon.
/// </summary>
public sealed class PutSetProfileIcon : CosmeticsWriteEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/set_profile_icon");
    }

    protected override async Task<JsonNode?> Answer(ICosmeticsService cosmetics, string accountId, JsonObject body, CancellationToken ct) =>
        await cosmetics.SetProfileIconAsync(accountId, body["Slug"], ct) ? new JsonObject { ["EquippedProfileIcon"] = body["Slug"]?.DeepClone() } : null;
}

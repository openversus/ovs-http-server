using FastEndpoints;
using OpenVersus.Server.Core.Perks;
using OpenVersus.Server.Http.Shared.Endpoints;
using OpenVersus.Server.Http.Shared.Hosting;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/perks_set_character_page: saves one perk page of one character (<see cref="IPerksService.SetPageAsync"/>).
/// Seen in: binary ssc name; TS server: PUT /ssc/invoke/perks_set_character_page.
/// </summary>
public sealed class PutPerksSetCharacterPage : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/perks_set_character_page");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var (status, answer) = await Resolve<IPerksService>().SetPageAsync(
            HttpContext.Session()?.AccountId ?? "", await ReadBodyAsync(ct) as System.Text.Json.Nodes.JsonObject ?? [], ct);
        await SendJsonAsync(answer, status, ct);
    }
}

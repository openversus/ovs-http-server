using FastEndpoints;
using OpenVersus.Server.Core.Layouts;
using OpenVersus.Server.Http.Hosting;

namespace OpenVersus.Server.Http.Endpoints.Game.Layout;

/// <summary>
/// GET /layout/{layout_type}/personalized/{variant}/{id}: for dokken-layout-type and the variants the layout source has
/// (<see cref="ILayoutSource"/>), that variant's layout for the session's player; anything else goes on answering as a
/// stub (the TS server has no route for fighter-select-layout or fighter-bundle-content, which the game also asks for).
/// Seen in: binary 0x144fd9620; captured 96x; TS server: GET /layout/dokken-layout-type/personalized/account-cosmetics-variant/{id}, GET /layout/dokken-layout-type/personalized/battlepass-variant/{id}, GET /layout/dokken-layout-type/personalized/currency-variant/{id} ....
/// </summary>
public sealed class GetLayoutByLayoutTypePersonalizedByVariantById : StaticEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/layout/{layout_type}/personalized/{variant}/{id}");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var layouts = Resolve<ILayoutSource>();
        if (Route<string>("layout_type") != "dokken-layout-type" || Route<string>("variant") is not { } variant || !layouts.Variants.Contains(variant))
        {
            await SendNotPortedAsync();
            return;
        }

        var answer = await layouts.GetAsync(variant, HttpContext.Session()?.AccountId, HydraBodies.IsHydra(HttpContext), ct);
        if (answer.Hydra is { } hydra)
        {
            await HydraBodies.WriteEncodedAsync(HttpContext, hydra, ct);
        }
        else
        {
            await Send.StringAsync(answer.Json!, contentType: "application/json; charset=utf-8", cancellation: ct);
        }
    }
}

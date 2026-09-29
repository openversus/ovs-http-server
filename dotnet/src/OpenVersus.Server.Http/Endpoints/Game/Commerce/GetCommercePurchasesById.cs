using FastEndpoints;

namespace OpenVersus.Server.Http.Endpoints.Game.Commerce;

/// <summary>
/// GET /commerce/purchases/{id}: for "me", no purchases (Static/commerce-purchases-me.json), as the TS server answers;
/// it has no route for any other id. Seen in: binary 0x144fdb4d0; captured 22x; TS server: GET /commerce/purchases/me.
/// </summary>
public sealed class GetCommercePurchasesById : StaticEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/commerce/purchases/{id}");
    }

    public override Task HandleAsync(CancellationToken ct) =>
        Route<string>("id") == "me" ? SendStaticAsync("commerce-purchases-me", ct) : SendNotPortedAsync();
}

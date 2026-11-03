using System.Text.Json.Nodes;
using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Commerce;

/// <summary>
/// Any method /commerce/catalog/{id}/products/{product}/purchase.
/// Seen in: binary 0x144fdbda0.
/// Method not read yet; body has price_slug.
/// <para>
/// A Founder's Pack (founderspack-*) is refused with 409. The game claims those packs at login when Steam says the player
/// owns their DLC, which on the Internet Archive build's Steam emulator (Goldberg, every DLC owned) is every login. It
/// reads a 200 here as the player's own account, and the catch-all has no username in it: the name it sets is empty, and
/// with no Steam the game's sanitizer (the Null subsystem's, 3 s later) blanks the player's own name in the menus.
/// </para>
/// </summary>
public sealed class AnyCommerceCatalogByIdProductsByProductPurchase : TsCatchAllEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET, FastEndpoints.Http.PUT, FastEndpoints.Http.POST, FastEndpoints.Http.DELETE);
        Routes("/commerce/catalog/{id}/products/{product}/purchase");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (Route<string>("product")?.StartsWith("founderspack", StringComparison.OrdinalIgnoreCase) == true)
        {
            await SendJsonAsync(new JsonObject { ["error"] = "already_owned" }, 409, ct);
            return;
        }

        await base.HandleAsync(ct);
    }
}

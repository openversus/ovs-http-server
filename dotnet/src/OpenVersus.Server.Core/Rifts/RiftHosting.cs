using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using OpenVersus.Server.Core.Hosting;

namespace OpenVersus.Server.Core.Rifts;

public static class RiftHosting
{
    /// <summary>
    /// The rifts: today the TS server's frozen load_rifts answer (Static/ssc-load-rifts.json, classified in
    /// docs/fields/load-rifts.json). Its runtime data is per player on WB's servers and one fixed copy here.
    /// </summary>
    public static WebApplicationBuilder AddRifts(this WebApplicationBuilder builder)
    {
        builder.Services.AddFrozenAccountData("GET /ssc/invoke/load_rifts",
            "one copy of the rifts' runtime data (a new player's cauldrons and rewards; one account's tutorial progress and enemy teams)");
        return builder;
    }
}

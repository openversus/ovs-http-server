using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenVersus.Server.Core.Hosting;
using OpenVersus.Server.Core.Settings;

namespace OpenVersus.Server.Core.Rifts;

public static class RiftHosting
{
    /// <summary>
    /// The rifts: the player's rift state (<see cref="IRiftStateService"/>), the rift lobby (<see cref="IRiftLobbyService"/>), a rift node's match (<see cref="IRiftMatchService"/>, which needs AddMatchLauncher), and the TS server's frozen load_rifts answer
    /// (Static/ssc-load-rifts.json, classified in docs/fields/load-rifts.json), whose runtime data is per player on WB's
    /// servers and one fixed copy here.
    /// </summary>
    public static WebApplicationBuilder AddRifts(this WebApplicationBuilder builder)
    {
        builder.AddSetting<RiftSettings>("Rifts");
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IRiftStateService, RiftStateService>();
        builder.Services.AddSingleton<IRiftLobbyService, RiftLobbyService>();
        builder.Services.AddSingleton<IRiftMatchService, RiftMatchService>();
        builder.Services.AddFrozenAccountData("GET /ssc/invoke/load_rifts",
            "one copy of the rifts' runtime data (a new player's cauldrons and rewards; one account's tutorial progress and enemy teams)");
        builder.Services.AddFrozenAccountData("PUT /ssc/invoke/create_rift_lobby",
            "the chosen rift's entry of that same runtime data copy as the lobby's RuntimeData");
        builder.Services.AddFrozenAccountData("PUT /ssc/invoke/start_rift_node",
            "the node's bots (who they are, their starting damage) from that same runtime data copy");
        return builder;
    }
}

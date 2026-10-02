using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenVersus.Server.Core.Hosting;
using OpenVersus.Server.Core.Settings;

namespace OpenVersus.Server.Core.Rifts;

public static class RiftHosting
{
    /// <summary>
    /// The rift routes the HTTP service answers: the player's rift state (<see cref="IRiftStateService"/>), a rift node's
    /// match (<see cref="IRiftMatchService"/>, which needs AddMatchLauncher), each player's runtime data and what a rift
    /// match changes in it (<see cref="IRiftProgressService"/>; the results themselves: <see cref="AddRiftResults"/>), and
    /// the TS server's frozen load_rifts answer (Static/ssc-load-rifts.json, classified in docs/fields/load-rifts.json),
    /// whose runtime data is per player on WB's servers and one fixed copy here. The rift lobby: <see cref="AddRiftLobbies"/>.
    /// </summary>
    public static WebApplicationBuilder AddRifts(this WebApplicationBuilder builder)
    {
        AddRiftProgress(builder);
        builder.Services.AddSingleton<IRiftMatchService, RiftMatchService>();
        builder.Services.AddFrozenAccountData("GET /ssc/invoke/load_rifts",
            "each player's rift runtime data starts as one copy (its progress cleared): the enemy teams generated for one account");
        builder.Services.AddFrozenAccountData("PUT /ssc/invoke/start_rift_node",
            "the node's bots (who they are, their starting damage) from those same enemy teams");
        return builder;
    }

    /// <summary>
    /// The rift lobby (<see cref="IRiftLobbyService"/>), for the lobbies service; it reads the lobby settings AddPartyLobbies
    /// binds.
    /// </summary>
    public static WebApplicationBuilder AddRiftLobbies(this WebApplicationBuilder builder)
    {
        AddRiftProgress(builder);
        builder.Services.AddSingleton<IRiftLobbyService, RiftLobbyService>();
        builder.Services.AddFrozenAccountData("PUT /ssc/invoke/create_rift_lobby",
            "the chosen rift's entry of the player's runtime data, whose enemy teams are that copy's");
        return builder;
    }

    /// <summary>
    /// What a rift match's result changes (<see cref="RiftResultSubscriber"/>: the player's runtime data and rift state),
    /// for the match flow service. The rift routes stay with <see cref="AddRifts"/>.
    /// </summary>
    public static WebApplicationBuilder AddRiftResults(this WebApplicationBuilder builder)
    {
        AddRiftProgress(builder);
        builder.Services.AddHostedService<RiftResultSubscriber>();
        return builder;
    }

    // The rift state and runtime data, which both the routes and the results change.
    private static void AddRiftProgress(WebApplicationBuilder builder)
    {
        builder.AddSetting<RiftSettings>("Rifts");
        RiftCatalog.Configure(builder.Configuration.GetSection("Rifts").Get<RiftSettings>() ?? new RiftSettings());
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IRiftStateService, RiftStateService>();
        builder.Services.AddSingleton<IRiftProgressService, RiftProgressService>();
    }
}

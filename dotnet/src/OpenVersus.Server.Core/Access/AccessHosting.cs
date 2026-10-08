using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenVersus.Server.Core.Bans;
using OpenVersus.Server.Core.Hosting;
using OpenVersus.Server.Core.Settings;

namespace OpenVersus.Server.Core.Access;

public static class AccessHosting
{
    /// <summary>The game's login: its settings (Access, Realtime, WbNetwork, Bans, Seasons), the ban sources, name rules, person bans and the login service.</summary>
    public static WebApplicationBuilder AddAccess(this WebApplicationBuilder builder)
    {
        builder.AddSetting<AccessSettings>("Access");
        builder.AddSetting<RealtimeSettings>("Realtime");
        builder.AddSetting<WbNetworkSettings>("WbNetwork");
        // AccessService reads the current season: bound here, so it never falls back to the defaults unseen.
        builder.AddSetting<Seasons.SeasonSettings>("Seasons");
        // A Steam id Steam itself refused lately is a claim at the login (SteamSessions.RefusedRecentlyAsync).
        builder.AddSetting<Steam.SteamSettings>("Steam");
        // The OVS Dev badge's OVSDev stat (Inventory/Ownership.cs).
        builder.AddSetting<Inventory.OwnershipSettings>("Ownership");
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.AddBans();
        builder.Services.AddHostedService<BanLoader>();
        builder.Services.AddHostedService<BanSweep>();
        builder.Services.AddSingleton<IAccessService, AccessService>();
        builder.Services.AddFrozenAccountData("POST /access",
            "most of one captured account's profile (inventory, seasonal data, match history, level, perk preferences) and parts of its account (linked platforms, timestamps)");
        builder.Services.AddFrozenAccountData("POST /sessions/auth/token", "one captured WB account's id, username and public ids");
        return builder;
    }
}

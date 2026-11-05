using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenVersus.Server.Core.Settings;

namespace OpenVersus.Server.Core.Profiles;

public static class ProfilesHosting
{
    /// <summary>The profile lookups the game makes for the players it shows.</summary>
    public static WebApplicationBuilder AddProfiles(this WebApplicationBuilder builder)
    {
        // Presence from Steam, where the Steam identity service has a say (Steam:Enabled).
        builder.AddSetting<Steam.SteamSettings>("Steam");
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IProfilesService, ProfilesService>();
        return builder;
    }
}

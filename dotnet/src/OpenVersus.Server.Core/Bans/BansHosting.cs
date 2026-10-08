using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenVersus.Server.Core.Settings;

namespace OpenVersus.Server.Core.Bans;

public static class BansHosting
{
    /// <summary>
    /// What a service that checks names or makes bans needs: the Bans settings, the ban checks, the name lists and person
    /// bans. The import, the loads and the sweep run in the access service alone (AddAccess).
    /// </summary>
    public static WebApplicationBuilder AddBans(this WebApplicationBuilder builder)
    {
        builder.AddSetting<BanSettings>("Bans");
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.TryAddSingleton<IBanService, BanService>();
        builder.Services.TryAddSingleton<INameRules, NameRules>();
        builder.Services.TryAddSingleton<IPersonBans, PersonBans>();
        return builder;
    }
}

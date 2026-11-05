using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenVersus.Server.Core.Access;
using OpenVersus.Server.Core.Control;
using OpenVersus.Server.Core.Settings;
using OpenVersus.Server.Core.Steam;

namespace OpenVersus.Server.Identity.Steam;

public static class SteamIdentityHosting
{
    /// <summary>The session state machine over the registered <see cref="ISteamAuthClient"/> (the executable registers the SteamKit2 one before this).</summary>
    public static WebApplicationBuilder AddSteamAuthSessions(this WebApplicationBuilder builder)
    {
        builder.AddSetting<SteamSettings>("Steam");
        builder.AddSetting<AccessSettings>("Access");
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<SteamAuthSessions>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<SteamAuthSessions>());
        return builder;
    }

    /// <summary>The service's own control endpoints: <c>/control/steam/status</c> (the connection, the held sessions and the verdict counts).</summary>
    public static IEndpointRouteBuilder MapSteamIdentityControl(this IEndpointRouteBuilder routes)
    {
        var control = routes.MapControlGroup("/steam");
        control.MapGet("/status", async (SteamAuthSessions sessions) => Results.Ok(await sessions.StatusAsync()));
        return routes;
    }
}

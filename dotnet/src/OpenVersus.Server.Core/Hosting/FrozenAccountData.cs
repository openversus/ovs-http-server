using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace OpenVersus.Server.Core.Hosting;

/// <summary>
/// A response that still sends every player account data captured from WB's live servers (see
/// docs/FROZEN-ACCOUNT-DATA.md). Registered next to the service that serves it, so the entry goes away with that
/// service when the response starts computing its account parts; every registered entry is logged as a warning at
/// startup, so none is invisible.
/// </summary>
public sealed record FrozenAccountData(string Response, string What);

public static class FrozenAccountDataHosting
{
    public static IServiceCollection AddFrozenAccountData(this IServiceCollection services, string response, string what) =>
        services.AddSingleton(new FrozenAccountData(response, what));

    /// <summary>Logs a FROZEN ACCOUNT DATA warning for each registered entry.</summary>
    public static void LogFrozenAccountData(this WebApplication app)
    {
        foreach (var frozen in app.Services.GetServices<FrozenAccountData>())
        {
            app.Logger.LogWarning("FROZEN ACCOUNT DATA: {Response} sends every player {What}; see dotnet/docs/FROZEN-ACCOUNT-DATA.md",
                frozen.Response, frozen.What);
        }
    }
}

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace OpenVersus.Server.Core.Arenas;

public static class ArenaHosting
{
    /// <summary>The Arena lobby (<see cref="IArenaLobbyService"/>), for the lobbies service; it reads the lobby settings AddPartyLobbies binds.</summary>
    public static WebApplicationBuilder AddArenaLobbies(this WebApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IArenaLobbyService, ArenaLobbyService>();
        return builder;
    }

    /// <summary>
    /// The Arena itself (<see cref="IArenaService"/>) and its deadline loop, for the arenas service. Needs AddMatchLauncher.
    /// Bot-only matches are decided by <see cref="IArenaBotMatches"/> (a coin toss for now).
    /// </summary>
    public static WebApplicationBuilder AddArenas(this WebApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.TryAddSingleton<IArenaBotMatches, RandomArenaBotMatches>();
        builder.Services.AddSingleton<IArenaService, ArenaService>();
        builder.Services.AddHostedService<ArenaDeadlines>();
        return builder;
    }
}

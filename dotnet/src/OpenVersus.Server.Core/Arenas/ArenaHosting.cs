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
}

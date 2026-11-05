using FastEndpoints;
using OpenVersus.Server.Core.Arenas;
using OpenVersus.Server.Http.Shared.Endpoints;
using OpenVersus.Server.Http.Shared.Hosting;

namespace OpenVersus.Server.Lobbies.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/create_arena_lobby: the Arena lobby (<see cref="IArenaLobbyService"/>), made by the mode select's Arena
/// button.
/// Seen in: bench (2026-10-10). The TS server does not answer it.
/// </summary>
public sealed class PutCreateArenaLobby : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/create_arena_lobby");
    }

    public override async Task HandleAsync(CancellationToken ct) =>
        await SendJsonAsync(await Resolve<IArenaLobbyService>().CreateAsync(HttpContext.Session()?.Claims, (await ReadBodyAsync(ct))?.AsObject(), ct), ct);
}

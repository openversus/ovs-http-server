using FastEndpoints;
using Microsoft.Extensions.Logging;
using OpenVersus.Server.Core.Arenas;
using OpenVersus.Server.Http.Shared.Endpoints;
using OpenVersus.Server.Http.Shared.Hosting;

namespace OpenVersus.Server.Lobbies.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/join_arena_lobby: an accepted Arena invite (<see cref="IArenaLobbyService"/>).
/// Seen in: bench (2026-10-10, the game's answer to an invite with ContextData.LobbyType "Arena"). The TS server does not
/// answer it.
/// </summary>
public sealed class PutJoinArenaLobby : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/join_arena_lobby");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var body = (await ReadBodyAsync(ct))?.AsObject();
        var session = HttpContext.Session();
        Logger.LogInformation("join_arena_lobby from {Player}: LobbyTemplate {Template}", session?.AccountId, PartyEndpoint.LobbyTemplate(body));
        // The whole request, for reading what the game sends (it carries account ids: Debug, off unless Log:Level is raised).
        Logger.LogDebug("join_arena_lobby body: {Body}", body?.ToJsonString() ?? "none");
        await SendJsonAsync(await Resolve<IArenaLobbyService>().JoinAsync(session?.Claims, body, ct), ct);
    }
}

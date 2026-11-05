using FastEndpoints;
using OpenVersus.Server.Core.Rifts;
using OpenVersus.Server.Http.Shared.Endpoints;
using OpenVersus.Server.Http.Shared.Hosting;

namespace OpenVersus.Server.Lobbies.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/lock_rift_lobby_loadout: the player's character and skin for the rift ("Auto equip &amp; fight!";
/// <see cref="IRiftLobbyService.LockLoadoutAsync"/>).
/// Seen in: binary ssc name; capture (bench, 2026-09-30). The TS server does not answer it.
/// </summary>
public sealed class PutLockRiftLobbyLoadout : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/lock_rift_lobby_loadout");
    }

    public override async Task HandleAsync(CancellationToken ct) =>
        await SendJsonAsync(await Resolve<IRiftLobbyService>().LockLoadoutAsync(HttpContext.Session()?.Claims, (await ReadBodyAsync(ct))?.AsObject(), ct), ct);
}

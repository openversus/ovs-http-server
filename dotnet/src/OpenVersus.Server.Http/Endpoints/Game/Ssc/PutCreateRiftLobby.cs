using FastEndpoints;
using OpenVersus.Server.Core.Rifts;
using OpenVersus.Server.Http.Hosting;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/create_rift_lobby: the lobby a rift is played from (<see cref="IRiftLobbyService"/>), made on
/// "Traverse Rift".
/// Seen in: binary ssc name; capture (bench, 2026-09-30). The TS server does not answer it.
/// </summary>
public sealed class PutCreateRiftLobby : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/create_rift_lobby");
    }

    public override async Task HandleAsync(CancellationToken ct) =>
        await SendJsonAsync(await Resolve<IRiftLobbyService>().CreateAsync(HttpContext.Session()?.Claims, (await ReadBodyAsync(ct))?.AsObject(), ct), ct);
}

using System.Text.Json.Nodes;
using FastEndpoints;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/game_install: the game reports its install; answered with an empty body and nothing kept, as the TS server does (ssc/routes.ts).
/// Seen in: binary ssc name; TS server: PUT /ssc/invoke/game_install.
/// Follow-up: docs/SSC.md (what the game sends, and what is left to do).
/// </summary>
public sealed class PutGameInstall : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/game_install");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        // Not stored yet (docs/SSC.md): logged so its contents can be read before deciding what to keep.
        Logger.LogInformation("game_install body: {Body}", (await ReadBodyAsync(ct))?.ToJsonString());
        await SendJsonAsync(new JsonObject { ["body"] = new JsonObject(), ["metadata"] = null, ["return_code"] = 0 }, ct);
    }
}

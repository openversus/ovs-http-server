using System.Text.Json.Nodes;
using FastEndpoints;
using OpenVersus.Server.Http.Shared.Endpoints;

namespace OpenVersus.Server.Lobbies.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/set_lobby_joinable: marks the lobby joinable: nothing is kept, the answer is empty (the TS server's). Seen in: binary ssc name; captured 5x; TS server.
/// </summary>
public sealed class PutSetLobbyJoinable : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/set_lobby_joinable");
    }

    public override Task HandleAsync(CancellationToken ct) =>
        SendJsonAsync(new JsonObject { ["body"] = new JsonObject(), ["metadata"] = null, ["return_code"] = 0 }, ct);
}

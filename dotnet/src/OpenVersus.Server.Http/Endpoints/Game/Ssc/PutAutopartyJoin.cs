using System.Text.Json.Nodes;
using FastEndpoints;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/autoparty_join: the game's auto-party: not a feature here, the answer is empty (the TS server's). Seen in: binary ssc name; TS server.
/// </summary>
public sealed class PutAutopartyJoin : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/autoparty_join");
    }

    public override Task HandleAsync(CancellationToken ct) =>
        SendJsonAsync(new JsonObject { ["body"] = new JsonObject(), ["metadata"] = null, ["return_code"] = 0 }, ct);
}

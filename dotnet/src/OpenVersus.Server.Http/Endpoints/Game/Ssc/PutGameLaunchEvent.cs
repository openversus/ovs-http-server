using FastEndpoints;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/game_launch_event: the game reports its launch; answered with nothing: status 200, text/html, no body, to a Hydra request too, as the TS server's res.send("") (handlers/ssc.ts).
/// Seen in: binary ssc name; captured 11x; TS server: PUT /ssc/invoke/game_launch_event.
/// Follow-up: docs/SSC.md (what the game sends, and what is left to do).
/// </summary>
public sealed class PutGameLaunchEvent : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/game_launch_event");
    }

    public override Task HandleAsync(CancellationToken ct) =>
        Send.StringAsync("", contentType: "text/html; charset=utf-8", cancellation: ct);
}

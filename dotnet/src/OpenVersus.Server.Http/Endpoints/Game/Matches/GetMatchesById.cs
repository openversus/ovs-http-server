using FastEndpoints;
using Microsoft.Extensions.Options;
using OpenVersus.Server.Core.CustomLobbies;
using OpenVersus.Server.Http.Hosting;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Matches;

/// <summary>
/// GET /matches/{id}: a custom lobby code (10 characters or fewer, any case; PUT /ssc/invoke/lobby_code) answers the
/// lobby's match document (<see cref="ICustomLobbyService.ByCodeAsync"/>). Anything else is not ported: it answers as a
/// stub does (the TS server had no route for it either, and answered its catch-all).
/// Seen in: binary 0x144fda970; TS server: GET /matches/{id}.
/// </summary>
public sealed class GetMatchesById : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/matches/{id}");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        string id = Route<string>("id") ?? "";
        if (await Resolve<ICustomLobbyService>().ByCodeAsync(id, ct) is { } match)
        {
            Logger.LogInformation("Lobby code {Code} resolved to {Lobby}", id, match["id"]);
            await SendJsonAsync(match, ct);
            return;
        }

        Logger.LogWarning("Not ported: GET /matches/{Id} (not a lobby code)", id);
        HttpContext.Response.Headers[Stub.Header] = nameof(GetMatchesById);
        await Send.ResultAsync(Results.StatusCode(Resolve<IOptionsMonitor<StubSettings>>().CurrentValue.StatusCode));
    }
}

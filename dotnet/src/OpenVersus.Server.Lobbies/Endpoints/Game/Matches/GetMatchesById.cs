using FastEndpoints;
using OpenVersus.Server.Core.CustomLobbies;
using OpenVersus.Server.Http.Shared.Endpoints;
using OpenVersus.Server.Http.Shared.Hosting;

namespace OpenVersus.Server.Lobbies.Endpoints.Game.Matches;

/// <summary>
/// GET /matches/{id}: a custom lobby code (10 characters or fewer, any case; PUT /ssc/invoke/lobby_code) answers the
/// lobby's match document (<see cref="ICustomLobbyService.ByCodeAsync"/>). Anything else falls through in the TS server
/// (next(): no route, its catch-all), and so here.
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

        await SendTsCatchAllAsync(ct);
    }
}

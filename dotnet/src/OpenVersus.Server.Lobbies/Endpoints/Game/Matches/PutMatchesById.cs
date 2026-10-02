using FastEndpoints;
using OpenVersus.Server.Core.Matches;
using OpenVersus.Server.Http.Shared.Endpoints;
using OpenVersus.Server.Http.Shared.Hosting;

namespace OpenVersus.Server.Lobbies.Endpoints.Game.Matches;

/// <summary>
/// PUT /matches/{id}: the party lobby the game fetches after create_party_lobby, or joins (<see cref="IPartyLobbyService"/>).
/// The body (player_data with empty game_server_region_data) is not read, as there.
/// Seen in: binary 0x144fde060; captured 11x; TS server: PUT /matches/{id}.
/// </summary>
public sealed class PutMatchesById : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/matches/{id}");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var claims = HttpContext.Session()?.Claims;
        string Claim(string name) => claims?[name] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
        var player = new LobbyPlayer(Claim("id"), Claim("hydraUsername"), Claim("username"), Claim("wb_network_id"), Claim("steamId"), Claim("epicId"));
        if (player.Id.Length == 0)
        {
            await Send.ResultAsync(Results.Json(new { error = "Invalid access token" }, statusCode: StatusCodes.Status401Unauthorized));
            return;
        }

        if (await Resolve<IPartyLobbyService>().PutAsync(player, Route<string>("id") ?? "", ct) is { } lobby)
        {
            await SendJsonAsync(lobby, ct);
        }
        else
        {
            await Send.ResultAsync(Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
        }
    }
}

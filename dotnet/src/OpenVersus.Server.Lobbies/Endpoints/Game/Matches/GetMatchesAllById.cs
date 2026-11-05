using FastEndpoints;
using OpenVersus.Server.Core.Matches;
using OpenVersus.Server.Http.Shared.Endpoints;

namespace OpenVersus.Server.Lobbies.Endpoints.Game.Matches;

/// <summary>
/// GET /matches/all/{id}: the player's recent matches, oldest first (<see cref="IMatchHistoryService"/>); the game asks
/// with count, page, fields=server_data and a list of templates, of which count and page are read.
/// Seen in: binary 0x144fdab70; captured 8x; TS server: GET /matches/all/{id}.
/// </summary>
public sealed class GetMatchesAllById : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/matches/all/{id}");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var query = HttpContext.Request.Query;
        // Express hands parseInt a repeated value as an array, which it reads joined with commas.
        string? count = query.TryGetValue("count", out var c) ? string.Join(",", c.ToArray()) : null;
        string? page = query.TryGetValue("page", out var p) ? string.Join(",", p.ToArray()) : null;
        if (await Resolve<IMatchHistoryService>().AllAsync(Route<string>("id")!, count, page, ct) is { } matches)
        {
            await SendJsonAsync(matches, ct);
        }
        else
        {
            await Send.ResultAsync(Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
        }
    }
}

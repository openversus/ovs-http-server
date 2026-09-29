using FastEndpoints;
using OpenVersus.Server.Core.Profiles;
using OpenVersus.Server.Http.Hosting;

namespace OpenVersus.Server.Http.Endpoints.Game.Profiles;

/// <summary>
/// GET /profiles/search_queries/{id}/run: for get-by-username, the players whose name contains ?username=
/// (<see cref="IProfilesService.SearchAsync"/>); any other search query goes on answering as a stub (the TS server has
/// only this one).
/// Seen in: binary 0x145066420; TS server: GET /profiles/search_queries/get-by-username/run.
/// </summary>
public sealed class GetProfilesSearchQueriesByIdRun : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/profiles/search_queries/{id}/run");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (Route<string>("id") != "get-by-username")
        {
            await SendNotPortedAsync();
            return;
        }

        // A repeated username: the first (the TS server's Mongo query fails on the array and answers no results).
        string? username = HttpContext.Request.Query["username"].FirstOrDefault();
        await SendJsonAsync(await Resolve<IProfilesService>().SearchAsync(username, HydraBodies.IsHydra(HttpContext), ct), ct);
    }
}

using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Profiles;

/// <summary>
/// GET /profiles/search_queries/{id}/run.
/// Seen in: binary 0x145066420; TS server: GET /profiles/search_queries/get-by-username/run.
/// </summary>
public sealed class GetProfilesSearchQueriesByIdRun : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/profiles/search_queries/{id}/run");
    }
}

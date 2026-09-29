using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// GET /ssc/invoke/perks_get_all_pages.
/// Seen in: binary ssc name; TS server: GET /ssc/invoke/perks_get_all_pages.
/// Ssc: server/capture.
/// </summary>
public sealed class GetPerksGetAllPages : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/ssc/invoke/perks_get_all_pages");
    }
}

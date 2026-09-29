using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// Any method /ssc/invoke/get_active_ranked_seasons.
/// Seen in: binary ssc name.
/// Ssc: binary.
/// </summary>
public sealed class AnyGetActiveRankedSeasons : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET, FastEndpoints.Http.PUT, FastEndpoints.Http.POST, FastEndpoints.Http.DELETE);
        Routes("/ssc/invoke/get_active_ranked_seasons");
    }
}

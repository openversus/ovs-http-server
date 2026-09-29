using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// Any method /ssc/invoke/rift_reset_all_chapters.
/// Seen in: binary ssc name.
/// Ssc: binary (probable).
/// </summary>
public sealed class AnyRiftResetAllChapters : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET, FastEndpoints.Http.PUT, FastEndpoints.Http.POST, FastEndpoints.Http.DELETE);
        Routes("/ssc/invoke/rift_reset_all_chapters");
    }
}

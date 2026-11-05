using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// Any method /ssc/invoke/finish_rift_chapter.
/// Seen in: binary ssc name.
/// Ssc: binary (probable).
/// </summary>
public sealed class AnyFinishRiftChapter : TsCatchAllEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET, FastEndpoints.Http.PUT, FastEndpoints.Http.POST, FastEndpoints.Http.DELETE);
        Routes("/ssc/invoke/finish_rift_chapter");
    }
}

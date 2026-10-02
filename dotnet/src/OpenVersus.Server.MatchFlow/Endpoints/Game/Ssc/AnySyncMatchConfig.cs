using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.MatchFlow.Endpoints.Game.Ssc;

/// <summary>
/// Any method /ssc/invoke/sync_match_config.
/// Seen in: binary ssc name.
/// Ssc: binary.
/// </summary>
public sealed class AnySyncMatchConfig : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET, FastEndpoints.Http.PUT, FastEndpoints.Http.POST, FastEndpoints.Http.DELETE);
        Routes("/ssc/invoke/sync_match_config");
    }
}

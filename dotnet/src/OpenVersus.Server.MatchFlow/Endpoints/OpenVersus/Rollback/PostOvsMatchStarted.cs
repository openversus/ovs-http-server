using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.MatchFlow.Endpoints.OpenVersus.Rollback;

/// <summary>
/// POST /ovs_match_started.
/// Seen in: TS server: POST /ovs_match_started.
/// Server only.
/// </summary>
public sealed class PostOvsMatchStarted : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.POST);
        Routes("/ovs_match_started");
    }
}

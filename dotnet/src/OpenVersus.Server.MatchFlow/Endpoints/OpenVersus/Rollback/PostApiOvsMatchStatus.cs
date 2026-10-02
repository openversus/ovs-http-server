using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.MatchFlow.Endpoints.OpenVersus.Rollback;

/// <summary>
/// POST /api/ovs_match_status.
/// Seen in: TS server: POST /api/ovs_match_status.
/// Server only.
/// </summary>
public sealed class PostApiOvsMatchStatus : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.POST);
        Routes("/api/ovs_match_status");
    }
}

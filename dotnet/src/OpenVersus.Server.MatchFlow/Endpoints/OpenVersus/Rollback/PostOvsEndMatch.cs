using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.MatchFlow.Endpoints.OpenVersus.Rollback;

/// <summary>
/// POST /ovs_end_match.
/// Seen in: TS server: POST /ovs_end_match.
/// Server only.
/// </summary>
public sealed class PostOvsEndMatch : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.POST);
        Routes("/ovs_end_match");
    }
}

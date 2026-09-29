using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.OpenVersus.Rollback;

/// <summary>
/// POST /ovs_match_status.
/// Seen in: TS server: POST /ovs_match_status.
/// Server only.
/// </summary>
public sealed class PostOvsMatchStatus : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.POST);
        Routes("/ovs_match_status");
    }
}

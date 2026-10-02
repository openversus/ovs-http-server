using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Http.Endpoints.OpenVersus.Rollback;

/// <summary>
/// POST /mvsi_end_match.
/// Seen in: TS server: POST /mvsi_end_match.
/// Server only.
/// </summary>
public sealed class PostMvsiEndMatch : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.POST);
        Routes("/mvsi_end_match");
    }
}

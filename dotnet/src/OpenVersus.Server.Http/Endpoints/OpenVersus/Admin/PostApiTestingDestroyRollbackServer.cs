using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.OpenVersus.Admin;

/// <summary>
/// POST /api/testing/destroy-rollback-server.
/// Seen in: TS server: POST /api/testing/destroy-rollback-server.
/// Server only.
/// </summary>
public sealed class PostApiTestingDestroyRollbackServer : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.POST);
        Routes("/api/testing/destroy-rollback-server");
    }
}

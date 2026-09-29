using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.OpenVersus.Rollback;

/// <summary>
/// POST /ovs_register.
/// Seen in: TS server: POST /ovs_register.
/// Server only.
/// </summary>
public sealed class PostOvsRegister : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.POST);
        Routes("/ovs_register");
    }
}

using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Http.Endpoints.OpenVersus.Rollback;

/// <summary>
/// POST /mvsi_register.
/// Seen in: TS server: POST /mvsi_register.
/// Server only.
/// </summary>
public sealed class PostMvsiRegister : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.POST);
        Routes("/mvsi_register");
    }
}

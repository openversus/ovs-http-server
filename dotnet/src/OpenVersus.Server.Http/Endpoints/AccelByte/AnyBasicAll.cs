using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.AccelByte;

/// <summary>
/// Any method /basic/*.
/// Seen in: TS server: ALL /basic/*.
/// Server only.
/// </summary>
public sealed class AnyBasicAll : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET, FastEndpoints.Http.PUT, FastEndpoints.Http.POST, FastEndpoints.Http.DELETE);
        Routes("/basic/{**rest}");
    }
}

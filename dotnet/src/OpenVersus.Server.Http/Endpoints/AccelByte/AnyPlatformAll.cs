using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.AccelByte;

/// <summary>
/// Any method /platform/*.
/// Seen in: TS server: ALL /platform/*.
/// Server only.
/// </summary>
public sealed class AnyPlatformAll : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET, FastEndpoints.Http.PUT, FastEndpoints.Http.POST, FastEndpoints.Http.DELETE);
        Routes("/platform/{**rest}");
    }
}

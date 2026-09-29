using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.AccelByte;

/// <summary>
/// Any method /social/*.
/// Seen in: TS server: ALL /social/*.
/// Server only.
/// </summary>
public sealed class AnySocialAll : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET, FastEndpoints.Http.PUT, FastEndpoints.Http.POST, FastEndpoints.Http.DELETE);
        Routes("/social/{**rest}");
    }
}

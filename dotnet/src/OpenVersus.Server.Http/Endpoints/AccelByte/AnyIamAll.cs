using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.AccelByte;

/// <summary>
/// Any method /iam/*.
/// Seen in: TS server: ALL /iam/*.
/// Server only.
/// </summary>
public sealed class AnyIamAll : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET, FastEndpoints.Http.PUT, FastEndpoints.Http.POST, FastEndpoints.Http.DELETE);
        Routes("/iam/{**rest}");
    }
}

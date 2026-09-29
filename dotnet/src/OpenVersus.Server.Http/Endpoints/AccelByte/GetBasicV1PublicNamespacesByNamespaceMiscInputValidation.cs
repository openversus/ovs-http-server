using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.AccelByte;

/// <summary>
/// GET /basic/v1/public/namespaces/{namespace}/misc/input/validation.
/// Seen in: TS server: GET /basic/v1/public/namespaces/{namespace}/misc/input/validation.
/// Server only.
/// </summary>
public sealed class GetBasicV1PublicNamespacesByNamespaceMiscInputValidation : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/basic/v1/public/namespaces/{namespace}/misc/input/validation");
    }
}

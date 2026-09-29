using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.AccelByte;

/// <summary>
/// GET /iam/v3/public/namespaces/{namespace}.
/// Seen in: TS server: GET /iam/v3/public/namespaces/{namespace}.
/// Server only.
/// </summary>
public sealed class GetIamV3PublicNamespacesByNamespace : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/iam/v3/public/namespaces/{namespace}");
    }
}

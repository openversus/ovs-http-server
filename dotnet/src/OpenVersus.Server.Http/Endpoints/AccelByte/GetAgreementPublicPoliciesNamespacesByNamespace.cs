using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.AccelByte;

/// <summary>
/// GET /agreement/public/policies/namespaces/{namespace}.
/// Seen in: TS server: GET /agreement/public/policies/namespaces/{namespace}.
/// Server only.
/// </summary>
public sealed class GetAgreementPublicPoliciesNamespacesByNamespace : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/agreement/public/policies/namespaces/{namespace}");
    }
}

using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Web.Endpoints.OpenVersus.Admin;

/// <summary>
/// POST /syncAsset.
/// Seen in: TS server: POST /syncAsset.
/// Server only.
/// </summary>
public sealed class PostSyncAsset : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.POST);
        Routes("/syncAsset");
    }
}

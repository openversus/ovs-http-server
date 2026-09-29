using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Configuration;

/// <summary>
/// Any method /configuration/sdk.
/// Seen in: binary fragment.
/// Binary fragment; no builder found yet.
/// </summary>
public sealed class AnyConfigurationSdk : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET, FastEndpoints.Http.PUT, FastEndpoints.Http.POST, FastEndpoints.Http.DELETE);
        Routes("/configuration/sdk");
    }
}

using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Datarouter;

/// <summary>
/// POST /datarouter/api/v1/public/data/clients.
/// Seen in: binary string; TS server: POST /datarouter/api/v1/public/data/clients.
/// Unreal DataRouter (engine telemetry); exe holds 'datarouter/api/v1/public/data?SessionID='.
/// </summary>
public sealed class PostDatarouterApiV1PublicDataClients : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.POST);
        Routes("/datarouter/api/v1/public/data/clients");
    }
}

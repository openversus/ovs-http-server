using FastEndpoints;
using OpenVersus.Server.Http.Hosting;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.GlobalConfigurationTypes;

/// <summary>
/// GET /global_configuration_types/{type}/global_configurations/{id}.
/// Seen in: binary 0x14505c830; TS server: GET /global_configuration_types/eula/global_configurations/*.
/// </summary>
[NoHydraToken(RouteValue = "type", Value = "eula")]
public sealed class GetGlobalConfigurationTypesByTypeGlobalConfigurationsById : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/global_configuration_types/{type}/global_configurations/{id}");
    }
}

using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.GlobalConfigurationTypes;

/// <summary>
/// GET /global_configuration_types/{type}/global_configurations.
/// Seen in: binary 0x14505caf0; TS server: GET /global_configuration_types/calendarflags/global_configurations, GET /global_configuration_types/wwshopconfiguration/global_configurations.
/// </summary>
public sealed class GetGlobalConfigurationTypesByTypeGlobalConfigurations : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/global_configuration_types/{type}/global_configurations");
    }
}

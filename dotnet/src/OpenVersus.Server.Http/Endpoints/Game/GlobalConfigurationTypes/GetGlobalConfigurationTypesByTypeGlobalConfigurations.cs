using FastEndpoints;
using OpenVersus.Server.Http.Shared.Endpoints;

namespace OpenVersus.Server.Http.Endpoints.Game.GlobalConfigurationTypes;

/// <summary>
/// GET /global_configuration_types/{type}/global_configurations: for calendarflags and wwshopconfiguration, an empty body
/// as the TS server answers them (res.send(""): text/html, even to a Hydra request, which the game accepts). Other types
/// are not answered (the TS server has no route for them). Both are in the login batch.
/// Seen in: binary 0x14505caf0; TS server: GET /global_configuration_types/calendarflags/global_configurations, GET /global_configuration_types/wwshopconfiguration/global_configurations.
/// </summary>
public sealed class GetGlobalConfigurationTypesByTypeGlobalConfigurations : StaticEndpoint
{
    private static readonly HashSet<string> s_answered = new(["calendarflags", "wwshopconfiguration"], StringComparer.OrdinalIgnoreCase);

    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/global_configuration_types/{type}/global_configurations");
    }

    public override Task HandleAsync(CancellationToken ct) =>
        s_answered.Contains(Route<string>("type") ?? "")
            ? Send.StringAsync("", contentType: "text/html; charset=utf-8", cancellation: ct)
            : SendNotPortedAsync();
}

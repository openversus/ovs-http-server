using System.Text.Json.Nodes;
using FastEndpoints;
using OpenVersus.Server.Http.Shared.Endpoints;
using OpenVersus.Server.Http.Shared.Hosting;

namespace OpenVersus.Server.Http.Endpoints.Game.GlobalConfigurationTypes;

/// <summary>
/// GET /global_configuration_types/{type}/global_configurations/{id}: the eula configurations (asked before the game has
/// a session, so without a token) are answered as the TS server answers them, the JSON number 200 (its res.json(200));
/// another type has no TS route (its catch-all, token required).
/// Seen in: binary 0x14505c830; TS server: GET /global_configuration_types/eula/global_configurations/*.
/// </summary>
[NoHydraToken(RouteValue = "type", Value = "eula")]
public sealed class GetGlobalConfigurationTypesByTypeGlobalConfigurationsById : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/global_configuration_types/{type}/global_configurations/{id}");
    }

    public override Task HandleAsync(CancellationToken ct) =>
        Route<string>("type") == "eula" ? SendJsonAsync(JsonValue.Create(200), ct) : SendTsCatchAllAsync(ct);
}

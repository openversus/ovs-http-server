using System.Text.Json.Nodes;
using FastEndpoints;
using OpenVersus.Server.Http.Shared.Endpoints;
using OpenVersus.Server.Http.Shared.Hosting;

namespace OpenVersus.Server.Http.Endpoints.Game.GlobalConfigurationTypes;

/// <summary>
/// GET /global_configuration_types/{type}/global_configurations/{id}: the eula configurations (asked before the game has
/// a session, so without a token) are answered with an object holding the EULA's version and text; another type has no
/// TS route (its catch-all, token required).
/// The game asks for it on a first launch, before its login, and reads version (an integer) and text (a string) from
/// the body's object (the handler at binary 0x140cfcbc0). The TS server answered the JSON number 200 (its res.json(200)),
/// which parses to no object: the game then read through a null pointer and closed, every launch, for every player who
/// had not accepted the EULA on that install (2026-10-29: 21 players on launch night, none of them reaching /access).
/// The same fields are under data too, the shape Hydra's global configurations otherwise come in.
/// Seen in: binary 0x14505c830; TS server: GET /global_configuration_types/eula/global_configurations/*.
/// </summary>
[NoHydraToken(RouteValue = "type", Value = "eula")]
public sealed class GetGlobalConfigurationTypesByTypeGlobalConfigurationsById : JsonBodyEndpoint
{
    /// <summary>The EULA version: the one every account's data records as accepted (EULAAcceptVersion).</summary>
    public const int EulaVersion = 5;

    /// <summary>The text the game shows.</summary>
    public const string EulaText =
        "OpenVersus is a free, community-run server for MultiVersus. It is not affiliated with or endorsed by Warner Bros. Games or Player First Games. "
        + "Play fair and be respectful: cheating, harassment and abuse can get an account banned.";

    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/global_configuration_types/{type}/global_configurations/{id}");
    }

    public override Task HandleAsync(CancellationToken ct) =>
        Route<string>("type") == "eula" ? SendJsonAsync(Eula(), ct) : SendTsCatchAllAsync(ct);

    /// <summary>The eula configuration the game reads.</summary>
    public static JsonObject Eula() => new()
    {
        ["version"] = EulaVersion,
        ["text"] = EulaText,
        ["data"] = new JsonObject { ["version"] = EulaVersion, ["text"] = EulaText },
    };
}

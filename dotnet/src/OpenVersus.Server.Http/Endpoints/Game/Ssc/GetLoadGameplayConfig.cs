using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// GET /ssc/invoke/load_gameplay_config.
/// Seen in: binary ssc name; TS server: GET /ssc/invoke/load_gameplay_config.
/// Ssc: binary (probable).
/// </summary>
public sealed class GetLoadGameplayConfig : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/ssc/invoke/load_gameplay_config");
    }
}

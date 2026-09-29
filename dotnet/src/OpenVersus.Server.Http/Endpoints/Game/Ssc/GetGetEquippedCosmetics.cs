using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// GET /ssc/invoke/get_equipped_cosmetics.
/// Seen in: binary ssc name; TS server: GET /ssc/invoke/get_equipped_cosmetics.
/// Ssc: binary (probable).
/// </summary>
public sealed class GetGetEquippedCosmetics : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/ssc/invoke/get_equipped_cosmetics");
    }
}

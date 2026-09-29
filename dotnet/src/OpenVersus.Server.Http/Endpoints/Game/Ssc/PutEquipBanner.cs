using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/equip_banner.
/// Seen in: binary ssc name; captured 2x; TS server: PUT /ssc/invoke/equip_banner.
/// Ssc: binary (probable).
/// </summary>
public sealed class PutEquipBanner : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/equip_banner");
    }
}

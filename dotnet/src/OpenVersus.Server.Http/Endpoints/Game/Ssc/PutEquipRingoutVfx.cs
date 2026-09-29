using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/equip_ringout_vfx.
/// Seen in: binary ssc name; TS server: PUT /ssc/invoke/equip_ringout_vfx.
/// Ssc: binary (probable).
/// </summary>
public sealed class PutEquipRingoutVfx : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/equip_ringout_vfx");
    }
}

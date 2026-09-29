using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/equip_announcer_pack.
/// Seen in: binary ssc name; TS server: PUT /ssc/invoke/equip_announcer_pack.
/// Ssc: binary (probable).
/// </summary>
public sealed class PutEquipAnnouncerPack : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/equip_announcer_pack");
    }
}

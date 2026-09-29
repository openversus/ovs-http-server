using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/equip_stat_tracker.
/// Seen in: binary ssc name; TS server: PUT /ssc/invoke/equip_stat_tracker.
/// Ssc: binary (probable).
/// </summary>
public sealed class PutEquipStatTracker : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/equip_stat_tracker");
    }
}

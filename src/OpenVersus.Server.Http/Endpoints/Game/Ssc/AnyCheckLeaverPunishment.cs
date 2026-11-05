using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// Any method /ssc/invoke/check_leaver_punishment.
/// Seen in: binary ssc name.
/// Ssc: binary.
/// </summary>
public sealed class AnyCheckLeaverPunishment : TsCatchAllEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET, FastEndpoints.Http.PUT, FastEndpoints.Http.POST, FastEndpoints.Http.DELETE);
        Routes("/ssc/invoke/check_leaver_punishment");
    }
}

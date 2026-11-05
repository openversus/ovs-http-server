using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// Any method /ssc/invoke/convert_candy_to_gold.
/// Seen in: binary ssc name.
/// Ssc: binary.
/// </summary>
public sealed class AnyConvertCandyToGold : TsCatchAllEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET, FastEndpoints.Http.PUT, FastEndpoints.Http.POST, FastEndpoints.Http.DELETE);
        Routes("/ssc/invoke/convert_candy_to_gold");
    }
}

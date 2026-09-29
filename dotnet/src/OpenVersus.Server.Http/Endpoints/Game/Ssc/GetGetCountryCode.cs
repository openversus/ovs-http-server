using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// GET /ssc/invoke/get_country_code.
/// Seen in: binary ssc name; TS server: GET /ssc/invoke/get_country_code.
/// Ssc: binary.
/// </summary>
public sealed class GetGetCountryCode : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/ssc/invoke/get_country_code");
    }
}

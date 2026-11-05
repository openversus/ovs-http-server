using FastEndpoints;
using OpenVersus.Server.Http.Shared.Endpoints;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// GET /ssc/invoke/get_country_code: "US" for everyone, as the TS server answers (Static/ssc-get-country-code.json).
/// In the login batch. Seen in: binary ssc name; TS server: GET /ssc/invoke/get_country_code.
/// </summary>
public sealed class GetGetCountryCode : StaticEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/ssc/invoke/get_country_code");
    }

    public override Task HandleAsync(CancellationToken ct) => SendStaticAsync("ssc-get-country-code", ct);
}

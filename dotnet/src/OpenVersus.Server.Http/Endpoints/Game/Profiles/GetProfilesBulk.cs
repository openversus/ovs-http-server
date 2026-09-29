using FastEndpoints;
using OpenVersus.Server.Core.Profiles;
using OpenVersus.Server.Http.Hosting;

namespace OpenVersus.Server.Http.Endpoints.Game.Profiles;

/// <summary>
/// GET /profiles/bulk: the profiles of the ids asked (ProfilesService). Sent as PUT + x-hydra-http-method: GET (the TS
/// server routes it as PUT). Seen in: binary 0x145065060; captured 48x; TS server: PUT /profiles/bulk.
/// </summary>
public sealed class GetProfilesBulk : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/profiles/bulk");
    }

    public override async Task HandleAsync(CancellationToken ct) =>
        await SendJsonAsync(await Resolve<IProfilesService>().ProfilesAsync(await ReadBodyAsync(ct), HydraBodies.IsHydra(HttpContext), ct), ct);
}

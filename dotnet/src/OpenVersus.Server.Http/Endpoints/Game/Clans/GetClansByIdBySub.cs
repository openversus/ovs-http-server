using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Clans;

/// <summary>
/// GET /clans/{id}/{sub}.
/// Seen in: binary 0x145059f00.
/// Second segment's name not read yet.
/// </summary>
public sealed class GetClansByIdBySub : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/clans/{id}/{sub}");
    }
}

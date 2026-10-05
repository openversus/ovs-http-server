using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Web.Endpoints.OpenVersus.Client;

/// <summary>
/// GET /ovs/all-players.
/// Seen in: TS server: GET /ovs/all-players.
/// Server only.
/// </summary>
public sealed class GetOvsAllPlayers : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/ovs/all-players");
    }
}

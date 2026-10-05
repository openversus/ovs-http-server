using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Arenas;

/// <summary>
/// GET /arenas/{id}/groups/{group}/participants.
/// Seen in: binary 0x145053b10.
/// </summary>
public sealed class GetArenasByIdGroupsByGroupParticipants : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/arenas/{id}/groups/{group}/participants");
    }
}

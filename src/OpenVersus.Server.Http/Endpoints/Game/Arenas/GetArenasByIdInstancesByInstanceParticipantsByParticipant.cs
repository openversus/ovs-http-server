using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Arenas;

/// <summary>
/// GET /arenas/{id}/instances/{instance}/participants/{participant}.
/// Seen in: binary 0x1450535f0.
/// </summary>
public sealed class GetArenasByIdInstancesByInstanceParticipantsByParticipant : TsCatchAllEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/arenas/{id}/instances/{instance}/participants/{participant}");
    }
}

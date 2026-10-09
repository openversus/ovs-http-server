using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Seasons;

/// <summary>
/// GET /seasons/{id}/instances/{instance}/participants/{participant}.
/// Seen in: binary 0x145067d00.
/// </summary>
public sealed class GetSeasonsByIdInstancesByInstanceParticipantsByParticipant : TsCatchAllEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/seasons/{id}/instances/{instance}/participants/{participant}");
    }
}
